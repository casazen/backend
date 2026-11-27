using System.Globalization;
using System.Reflection;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T01, privacy rule (ADR-013-R2, AC-NF51-9, AC-NF51-12): the selfie, the photo of the document, the crops and the vectors are held
/// in memory only. After a verification that succeeds, one that fails and one that throws, none of their bytes appears in the logs, every array
/// the service held is zeroed, and the service has nothing that could write them (no database, no queue, no file) or return them.
/// </summary>
public class BiometricVerificationImagesNeverPersistedTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static DetectedFace Face(double areaRatio) => new(10, 10, 40, 40, 0.99f, new float[10], areaRatio);

    private sealed record Run(
        CapturingLogger<BiometricVerificationService> Log,
        ScriptedFaceDetector Detector,
        ScriptedFaceEmbedder Embedder,
        IReadOnlyList<string> ForbiddenInLogs,
        BiometricVerificationResult? Result);

    /// <summary>The text a log could hold if it wrote a picture or a vector: base64, hex, decimal lists, and the numbers of the vectors.</summary>
    private static IEnumerable<string> Forbidden(byte[] encoded, byte[] pixels, params float[][] vectors)
    {
        // Bytes from the middle of the compressed data (the signature of a PNG is the same for every picture).
        var fromFile = encoded.Skip(encoded.Length / 2).Take(16).ToArray();
        var fromPixels = pixels.Take(16).ToArray();
        yield return Convert.ToBase64String(encoded);
        foreach (var slice in new[] { fromFile, fromPixels })
        {
            yield return Convert.ToHexString(slice);
            yield return BitConverter.ToString(slice);
            yield return string.Join(",", slice);
            yield return string.Join(" ", slice);
        }

        foreach (var value in vectors.SelectMany(v => v))
        {
            yield return value.ToString("R", Invariant);
            yield return value.ToString("F6", Invariant);
        }
    }

    private static async Task<Run> Execute(
        string scenario,
        Func<float[], float[], (ScriptedFaceDetector Detector, ScriptedFaceEmbedder Embedder)> script,
        Func<byte[], string>? failureText = null)
    {
        var selfiePixels = TestImages.RandomRgb(48, 32, seed: 31);
        var documentPixels = TestImages.RandomRgb(64, 40, seed: 47);
        var selfiePng = TestImages.Png(48, 32, selfiePixels);
        var documentPng = TestImages.Png(64, 40, documentPixels);
        var selfieVector = new[] { 0.123457f, 0.654321f, 0.246810f, 0.135792f };
        var documentVector = new[] { 0.123457f, 0.654321f, 0.246810f, 0.135792f };

        // Taken before the run: the application zeroes the very arrays the test keeps.
        var forbidden = Forbidden(selfiePng, selfiePixels, selfieVector)
            .Concat(Forbidden(documentPng, documentPixels, documentVector))
            .Distinct()
            .ToList();

        var (detector, embedder) = script(selfieVector, documentVector);
        if (failureText is not null)
            embedder.FailOn = (2, new InvalidOperationException(failureText(selfiePng)));

        var antiSpoofing = new Mock<IAntiSpoofingService>();
        antiSpoofing
            .Setup(s => s.ScoreLivenessAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LivenessResult(true, 0.93));
        var log = new CapturingLogger<BiometricVerificationService>();
        var service = new BiometricVerificationService(
            antiSpoofing.Object, log, new FakeBiometricModelStore(), detector, embedder, Options.Create(new BiometricModelOptions()));

        var result = await service.VerifyAsync(new MemoryStream(selfiePng), new MemoryStream(documentPng), 0.85, 0.70);
        Assert.True(log.Entries.Count > 0, $"{scenario}: the service logged nothing, so the proof would be empty");
        return new Run(log, detector, embedder, forbidden, result);
    }

    private static void AssertNothingKept(string scenario, Run run)
    {
        var output = run.Log.AllOutput;
        foreach (var token in run.ForbiddenInLogs)
            Assert.DoesNotContain(token, output);

        // The exception itself is never handed to the logger: its message could carry what was being read.
        Assert.All(run.Log.Entries, entry => Assert.Null(entry.Exception));

        // Every picture and every vector the service held has been zeroed.
        Assert.NotEmpty(run.Detector.SeenPixels);
        foreach (var pixels in run.Detector.SeenPixels)
            Assert.True(pixels.All(b => b == 0), $"{scenario}: a decoded picture was left in memory");

        foreach (var vector in run.Embedder.Issued)
            Assert.True(vector.All(v => v == 0f), $"{scenario}: a face vector was left in memory");
    }

    [Fact]
    public async Task BiometricVerification_ImagesNeverPersisted_Test()
    {
        // Success: both faces found, the vectors compared, Accept.
        var success = await Execute(
            "success",
            (selfie, document) => (
                new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]),
                new ScriptedFaceEmbedder(selfie, document)));
        Assert.Equal(BiometricOutcome.Accept, success.Result!.Outcome);
        Assert.Equal(2, success.Embedder.Issued.Count);
        AssertNothingKept("success", success);

        // Failure: no face in the photo of the document, Reject.
        var failure = await Execute(
            "failure",
            (_, _) => (new ScriptedFaceDetector([Face(0.3)], []), new ScriptedFaceEmbedder()));
        Assert.Equal(BiometricOutcome.Reject, failure.Result!.Outcome);
        Assert.Equal(BiometricReasonCodes.NoFace, failure.Result.ReasonCode);
        AssertNothingKept("failure", failure);

        // Exception: the second vector cannot be made and the error text holds the whole selfie in base64, Escalate.
        var exception = await Execute(
            "exception",
            (selfie, document) => (
                new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]),
                new ScriptedFaceEmbedder(selfie, document)),
            failureText: selfiePng => "boom " + Convert.ToBase64String(selfiePng));
        Assert.Equal(BiometricOutcome.Escalate, exception.Result!.Outcome);
        Assert.Equal(BiometricReasonCodes.Error, exception.Result.ReasonCode);
        Assert.Single(exception.Embedder.Issued);
        AssertNothingKept("exception", exception);
    }

    [Fact]
    public void BiometricVerification_HasNothingThatCouldWriteAPicture()
    {
        // No database, no job queue, no e-mail queue, no object storage, no form file: the services only read streams and return numbers.
        var services = new[]
        {
            typeof(BiometricVerificationService), typeof(AntiSpoofingService), typeof(FaceDetector), typeof(FaceEmbedder),
            typeof(BiometricModelStore),
        };
        foreach (var service in services)
        {
            foreach (var constructor in service.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    var type = parameter.ParameterType;
                    Assert.False(typeof(DbContext).IsAssignableFrom(type), $"{service.Name} takes a database context");
                    Assert.DoesNotContain("Hangfire", type.Namespace ?? string.Empty, StringComparison.Ordinal);
                    Assert.DoesNotContain("Storage", type.Name, StringComparison.Ordinal);
                    Assert.DoesNotContain("Queue", type.Name, StringComparison.Ordinal);
                    Assert.DoesNotContain("FormFile", type.Name, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void BiometricVerificationResult_HoldsNumbersFlagsAndCodesOnly()
    {
        var allowed = new[] { typeof(bool), typeof(int), typeof(long), typeof(double), typeof(string), typeof(BiometricOutcome) };
        foreach (var property in typeof(BiometricVerificationResult).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Contains(property.PropertyType, allowed);

        // The numbers the agent needs (E03/F04/US01/T01) are all there.
        foreach (var name in new[]
                 {
                     "LivenessScore", "FaceInSelfie", "FaceInDocument", "FaceAreaRatioSelfie", "Similarity", "ModelName", "ModelVersion",
                     "DurationMs",
                 })
            Assert.NotNull(typeof(BiometricVerificationResult).GetProperty(name));

        // And the liveness result is a score and a flag.
        Assert.Equal(2, typeof(LivenessResult).GetProperties().Length);
    }
}
