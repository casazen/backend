using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US02/T02 proofs of the identity verification agent: <c>Ok</c> needs every check (AC-NF51-13), and nothing of the pictures or of the
/// person leaves the process through it (AC-NF51-9, AC-NF51-10).
/// </summary>
public class IdentityVerificationAgentProofTests
{
    private static readonly VerificationPolicy Policy = new(AcceptThreshold: 0.85, EscalateThreshold: 0.70, MaxAttempts: 3, Provisional: true);

    private static readonly DocumentCheck GoodDocument = new(
        MrzRead: true,
        CheckDigitsValid: true,
        Expired: false,
        NamesConsistent: true,
        BirthDateConsistent: true,
        DocumentNumberConsistent: true,
        NationalityConsistent: null,
        HolderIsMinor: false);

    private static readonly DetectedFace OneFace = new(20, 20, 120, 120, 0.99f, new float[10], 0.56);

    /// <summary>What the ADR-013 pipeline answers: by default a live selfie that matches the document well.</summary>
    private static BiometricVerificationResult Face(
        double similarity = 0.92,
        bool isLive = true,
        double livenessScore = 0.95,
        string? reasonCode = null,
        bool faceInDocument = true) => new()
        {
            Outcome = BiometricOutcome.Accept,
            Similarity = similarity,
            IsLive = isLive,
            LivenessScore = livenessScore,
            FaceInSelfie = true,
            FaceInDocument = faceInDocument,
            FaceAreaRatioSelfie = 0.56,
            ModelName = "AuraFace-v1",
            ModelVersion = "1.0.0",
            ReasonCode = reasonCode,
        };

    /// <summary>Trust rule (AC-NF51-13): the result is <c>Ok</c> with every check passed, and never when any single one fails.</summary>
    [Fact]
    public async Task IdentityVerificationAgent_OkOnlyWhenEveryCheckPassed_Test()
    {
        var everyCheck = await VerifyAsync(Face(), GoodDocument, [OneFace]);
        Assert.Equal(IdentityVerificationResult.Ok, everyCheck.Result);
        Assert.Equal(BiometricOutcome.Accept, everyCheck.BiometricOutcome);
        Assert.All(everyCheck.Reasons, r => Assert.Equal(VerificationReasons.Results.Passed, r.Result));
        Assert.Equal(IdentityVerificationGuestResults.Ok, everyCheck.GuestResult);
        Assert.True(everyCheck.Provisional);

        var oneFailure = new (string Check, BiometricVerificationResult Face, DocumentCheck Document, IReadOnlyList<DetectedFace> Faces, int Attempt)[]
        {
            ("no face in the selfie", Face(), GoodDocument, [], 1),
            ("two faces in the selfie", Face(), GoodDocument, [OneFace, OneFace], 1),
            ("face too small", Face(), GoodDocument, [OneFace with { AreaRatio = 0.01 }], 1),
            ("not live", Face(isLive: false, livenessScore: 0.2, reasonCode: BiometricReasonCodes.NotLive), GoodDocument, [OneFace], 1),
            ("live flag false", Face(isLive: false), GoodDocument, [OneFace], 1),
            ("similarity between the thresholds", Face(similarity: 0.78), GoodDocument, [OneFace], 1),
            ("similarity at the escalate threshold", Face(similarity: 0.70), GoodDocument, [OneFace], 1),
            ("similarity far under", Face(similarity: 0.20), GoodDocument, [OneFace], 3),
            ("document unreadable", Face(), GoodDocument with { MrzRead = false }, [OneFace], 1),
            ("document unreadable at the last attempt", Face(), GoodDocument with { MrzRead = false }, [OneFace], 3),
            ("check digits wrong", Face(), GoodDocument with { CheckDigitsValid = false }, [OneFace], 1),
            ("document expired", Face(), GoodDocument with { Expired = true }, [OneFace], 1),
            ("expiry unknown", Face(), GoodDocument with { Expired = null }, [OneFace], 1),
            ("names differ", Face(), GoodDocument with { NamesConsistent = false }, [OneFace], 1),
            ("names not compared", Face(), GoodDocument with { NamesConsistent = null }, [OneFace], 1),
            ("birth date differs", Face(), GoodDocument with { BirthDateConsistent = false }, [OneFace], 1),
            ("document number differs", Face(), GoodDocument with { DocumentNumberConsistent = false }, [OneFace], 1),
            ("nationality differs", Face(), GoodDocument with { NationalityConsistent = false }, [OneFace], 1),
            ("holder is a minor", Face(), GoodDocument with { HolderIsMinor = true }, [OneFace], 1),
            ("models unavailable", Face(reasonCode: BiometricReasonCodes.Unavailable), GoodDocument, [OneFace], 1),
            ("timeout", Face(reasonCode: BiometricReasonCodes.Timeout), GoodDocument, [OneFace], 1),
            ("pipeline error", Face(reasonCode: BiometricReasonCodes.Error), GoodDocument, [OneFace], 1),
            ("no face on the document", Face(reasonCode: BiometricReasonCodes.NoFace, faceInDocument: false), GoodDocument, [OneFace], 1),
        };

        foreach (var (check, face, document, faces, attempt) in oneFailure)
        {
            var outcome = await VerifyAsync(face, document, faces, attempt);
            Assert.True(outcome.Result != IdentityVerificationResult.Ok, $"'{check}' must not be Ok");
            Assert.NotEqual(BiometricOutcome.Accept, outcome.BiometricOutcome);
            Assert.NotEqual(IdentityVerificationGuestResults.Ok, outcome.GuestResult);
            Assert.True(outcome.OperatorPathOpen, $"'{check}' must leave the way to a person open");
        }

        // The mapping of ADR-013: a review is Escalate, a failed attempt is Reject and goes to the operator at the last one.
        Assert.Equal(BiometricOutcome.Escalate, (await VerifyAsync(Face(similarity: 0.78), GoodDocument, [OneFace])).BiometricOutcome);
        var lastFailed = await VerifyAsync(Face(similarity: 0.2), GoodDocument, [OneFace], attempt: 3);
        Assert.Equal(BiometricOutcome.Reject, lastFailed.BiometricOutcome);
        Assert.Equal(IdentityVerificationGuestResults.Operator, lastFailed.GuestResult);
        Assert.Equal(IdentityVerificationGuestResults.Retry, (await VerifyAsync(Face(similarity: 0.2), GoodDocument, [OneFace])).GuestResult);
    }

    /// <summary>
    /// Privacy rule (AC-NF51-9, AC-NF51-10): the agent and its capture checks depend on nothing that writes (no logger, database, storage, queue,
    /// push, job, HTTP client or AI provider), the request cannot even carry a name or a value of the document, the outcome holds no picture and
    /// no vector, and the decoded selfie is zeroed once the agent is done with it.
    /// </summary>
    [Fact]
    public async Task IdentityVerificationAgent_NoImageOrPersonalDataLeavesTheProcess_Test()
    {
        var writers = new[]
        {
            typeof(ILogger), typeof(ILoggerFactory), typeof(DbContext), typeof(AppDbContext), typeof(IFileStorage), typeof(IEmailQueue),
            typeof(IEmailService), typeof(IPushNotificationService), typeof(IBackgroundJobClient), typeof(IAiProvider), typeof(HttpClient),
            typeof(IHttpClientFactory),
        };
        foreach (var type in new[] { typeof(IdentityVerificationAgent), typeof(CaptureQualityAssessor) })
        {
            var parameters = type.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToList();
            Assert.DoesNotContain(
                parameters,
                p => writers.Any(w => w.IsAssignableFrom(p)) || (p.IsGenericType && p.GetGenericTypeDefinition() == typeof(ILogger<>)));
        }

        // What reaches the agent about the document are flags: no name, no number, no date.
        Assert.All(typeof(DocumentCheck).GetProperties(), p => Assert.True(p.PropertyType == typeof(bool) || p.PropertyType == typeof(bool?), p.Name));

        // The outcome carries codes and numbers: no picture, crop or vector, by type.
        Assert.DoesNotContain(
            typeof(IdentityVerificationOutcome).GetProperties().Select(p => p.PropertyType),
            t => t == typeof(byte[]) || t == typeof(float[]) || typeof(Stream).IsAssignableFrom(t));

        var detector = new FixedFaceDetector([OneFace]);
        var outcome = await VerifyAsync(Face(), GoodDocument, detector);

        Assert.Equal(IdentityVerificationResult.Ok, outcome.Result);
        Assert.NotEmpty(detector.SeenPixels);
        Assert.All(detector.SeenPixels, pixels => Assert.All(pixels, value => Assert.Equal(0, value)));

        var json = VerificationReasons.Serialize(outcome.Reasons) + outcome.Thresholds.ToJson();
        foreach (var personal in new[] { "Mario", "Rossi", "AB1234567", "1990" })
            Assert.DoesNotContain(personal, json, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<IdentityVerificationOutcome> VerifyAsync(
        BiometricVerificationResult face,
        DocumentCheck document,
        IReadOnlyList<DetectedFace> faces,
        int attempt = 1) =>
        VerifyAsync(face, document, new FixedFaceDetector(faces), attempt);

    private static async Task<IdentityVerificationOutcome> VerifyAsync(
        BiometricVerificationResult face,
        DocumentCheck document,
        FixedFaceDetector detector,
        int attempt = 1)
    {
        var options = Options.Create(new BiometricModelOptions());
        var agent = new IdentityVerificationAgent(new FixedBiometrics(face), new CaptureQualityAssessor(detector, options), options);
        using var selfie = new MemoryStream(TestImages.Png(160, 160, seed: 7));
        using var documentPhoto = new MemoryStream(TestImages.Png(160, 100, seed: 11));
        return await agent.VerifyAsync(new IdentityVerificationRequest(selfie, documentPhoto, document, Policy, attempt), CancellationToken.None);
    }

    /// <summary>A pipeline that answers a fixed result (the decision is the agent's, the pipeline is proved by its own tests).</summary>
    private sealed class FixedBiometrics(BiometricVerificationResult result) : IBiometricVerificationService
    {
        public Task<BiometricVerificationResult> VerifyAsync(
            Stream selfieStream,
            Stream documentPhotoStream,
            double acceptThreshold,
            double escalateThreshold,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    /// <summary>A detector that always finds the same faces and keeps the pixel arrays it was shown, to prove they are zeroed afterwards.</summary>
    private sealed class FixedFaceDetector(IReadOnlyList<DetectedFace> faces) : IFaceDetector
    {
        public List<byte[]> SeenPixels { get; } = [];

        public IReadOnlyList<DetectedFace> Detect(RasterImage image, double minScore, CancellationToken cancellationToken)
        {
            SeenPixels.Add(image.Rgb);
            return faces;
        }
    }
}
