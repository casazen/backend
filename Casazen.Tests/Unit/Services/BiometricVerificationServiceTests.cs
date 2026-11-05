using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E03/F04/US01/T01: the decision of the face check (ADR-013): the models are asked for faces, liveness and vectors, and the safe default is the
/// operator. The models are replaced by scripts, so these tests prove the rules of the service, not the weights.
/// </summary>
public class BiometricVerificationServiceTests
{
    private const double Accept = 0.85;
    private const double Escalate = 0.70;

    private static DetectedFace Face(double areaRatio) => new(10, 10, 40, 40, 0.99f, new float[10], areaRatio);

    private static IReadOnlyList<DetectedFace> FacesIf(bool present, double areaRatio)
    {
        if (present)
            return [Face(areaRatio)];

        return [];
    }

    private static Mock<IAntiSpoofingService> Liveness(bool live, double score)
    {
        var mock = new Mock<IAntiSpoofingService>();
        mock.Setup(s => s.ScoreLivenessAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LivenessResult(live, score));
        return mock;
    }

    private static BiometricVerificationService Service(
        IAntiSpoofingService antiSpoofing,
        IFaceDetector? detector,
        IFaceEmbedder? embedder,
        bool modelsAvailable = true,
        BiometricModelOptions? options = null) =>
        new(
            antiSpoofing,
            NullLogger<BiometricVerificationService>.Instance,
            new FakeBiometricModelStore(modelsAvailable),
            detector,
            embedder,
            Options.Create(options ?? new BiometricModelOptions()));

    private static Task<BiometricVerificationResult> Verify(BiometricVerificationService service, double accept = Accept, double escalate = Escalate) =>
        service.VerifyAsync(
            new MemoryStream(TestImages.Png(48, 32, seed: 1)),
            new MemoryStream(TestImages.Png(64, 40, seed: 2)),
            accept,
            escalate);

    /// <summary>A vector that has the cosine <paramref name="similarity"/> with [1, 0].</summary>
    private static float[] VectorWithSimilarity(double similarity) =>
        [(float)similarity, (float)Math.Sqrt(1 - (similarity * similarity)), 0f];

    [Fact]
    public async Task VerifyAsync_ModelsNotAvailable_EscalatesAndReadsNothing()
    {
        var selfie = new MemoryStream(TestImages.Png(48, 32, seed: 1));
        var document = new MemoryStream(TestImages.Png(64, 40, seed: 2));
        var detector = new ScriptedFaceDetector();
        var service = Service(Liveness(live: true, 0.99).Object, detector, new ScriptedFaceEmbedder(), modelsAvailable: false);

        var result = await service.VerifyAsync(selfie, document, Accept, Escalate);

        Assert.Equal(BiometricOutcome.Escalate, result.Outcome);
        Assert.Equal(BiometricReasonCodes.Unavailable, result.ReasonCode);
        Assert.False(result.IsLive);
        Assert.Equal(0.0, result.Similarity);
        Assert.Equal(0, selfie.Position);
        Assert.Equal(0, document.Position);
        Assert.Equal(0, detector.Calls);
        Assert.NotEmpty(result.ModelName);
        Assert.NotEmpty(result.ModelVersion);
    }

    [Fact]
    public async Task VerifyAsync_NoDetectorOrEmbedder_Escalates()
    {
        var service = Service(Liveness(live: true, 0.99).Object, detector: null, embedder: null);

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Escalate, result.Outcome);
        Assert.Equal(BiometricReasonCodes.Unavailable, result.ReasonCode);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task VerifyAsync_NoFaceInEitherPicture_RejectsWithNoFace(bool faceInSelfie, bool faceInDocument)
    {
        var detector = new ScriptedFaceDetector(FacesIf(faceInSelfie, 0.3), FacesIf(faceInDocument, 0.1));
        var service = Service(Liveness(live: true, 0.99).Object, detector, new ScriptedFaceEmbedder());

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Reject, result.Outcome);
        Assert.Equal(BiometricReasonCodes.NoFace, result.ReasonCode);
        Assert.Equal(faceInSelfie, result.FaceInSelfie);
        Assert.Equal(faceInDocument, result.FaceInDocument);
        Assert.Equal(0.0, result.Similarity);
    }

    [Fact]
    public async Task VerifyAsync_TwoFacesInTheSelfie_Rejects()
    {
        var detector = new ScriptedFaceDetector([Face(0.3), Face(0.2)], [Face(0.1)]);
        var embedder = new ScriptedFaceEmbedder();
        var service = Service(Liveness(live: true, 0.99).Object, detector, embedder);

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Reject, result.Outcome);
        Assert.Equal(BiometricReasonCodes.MultipleFaces, result.ReasonCode);
        Assert.Equal(0, embedder.Calls);
    }

    [Fact]
    public async Task VerifyAsync_FaceSmallerThanTheMinimum_Rejects()
    {
        var detector = new ScriptedFaceDetector([Face(0.02)], [Face(0.1)]);
        var service = Service(Liveness(live: true, 0.99).Object, detector, new ScriptedFaceEmbedder());

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Reject, result.Outcome);
        Assert.Equal(BiometricReasonCodes.FaceTooSmall, result.ReasonCode);
        Assert.Equal(0.02, result.FaceAreaRatioSelfie, 6);
    }

    [Fact]
    public async Task VerifyAsync_SelfieNotLive_RejectsAndNeverCompares()
    {
        var detector = new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]);
        var embedder = new ScriptedFaceEmbedder();
        var service = Service(Liveness(live: false, 0.12).Object, detector, embedder);

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Reject, result.Outcome);
        Assert.Equal(BiometricReasonCodes.NotLive, result.ReasonCode);
        Assert.False(result.IsLive);
        Assert.Equal(0.12, result.LivenessScore, 6);
        Assert.Equal(0, embedder.Calls);
    }

    [Theory]
    [InlineData(0.90, BiometricOutcome.Accept)]
    [InlineData(0.80, BiometricOutcome.Escalate)]
    [InlineData(0.60, BiometricOutcome.Reject)]
    public async Task VerifyAsync_SimilarityIsMappedWithTheThresholdsReceived(double similarity, BiometricOutcome expected)
    {
        var detector = new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]);
        var embedder = new ScriptedFaceEmbedder([1f, 0f, 0f], VectorWithSimilarity(similarity));
        var service = Service(Liveness(live: true, 0.93).Object, detector, embedder);

        var result = await Verify(service);

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(similarity, result.Similarity, 4);
        Assert.True(result.IsLive);
        Assert.Equal(0.93, result.LivenessScore, 6);
        Assert.True(result.FaceInSelfie);
        Assert.True(result.FaceInDocument);
        Assert.Null(result.ReasonCode);
        Assert.InRange(result.DurationMs, 0, 60_000);
    }

    [Theory]
    [InlineData(0.85, BiometricOutcome.Accept)]
    [InlineData(0.8499, BiometricOutcome.Escalate)]
    [InlineData(0.7001, BiometricOutcome.Escalate)]
    [InlineData(0.70, BiometricOutcome.Reject)]
    [InlineData(0.0, BiometricOutcome.Reject)]
    public void MapOutcome_AtTheThresholds_FollowsAdr013R4(double similarity, BiometricOutcome expected)
    {
        // Accept at or above the accept threshold, Reject at or under the escalate one, Escalate between the two.
        Assert.Equal(expected, BiometricVerificationService.MapOutcome(similarity, Accept, Escalate));
    }

    [Fact]
    public async Task VerifyAsync_SimilarityBelowZero_IsReportedAsZero()
    {
        var detector = new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]);
        var embedder = new ScriptedFaceEmbedder([1f, 0f], [-1f, 0f]);
        var service = Service(Liveness(live: true, 0.93).Object, detector, embedder);

        var result = await Verify(service);

        Assert.Equal(0.0, result.Similarity);
        Assert.Equal(BiometricOutcome.Reject, result.Outcome);
    }

    [Theory]
    [InlineData(0.70, 0.85)]
    [InlineData(0.85, 0.85)]
    [InlineData(1.2, 0.5)]
    [InlineData(0.9, -0.1)]
    [InlineData(double.NaN, 0.5)]
    public async Task VerifyAsync_ThresholdsThatCannotBeUsed_EscalateWithoutLookingAtTheImages(double accept, double escalate)
    {
        var detector = new ScriptedFaceDetector();
        var service = Service(Liveness(live: true, 0.99).Object, detector, new ScriptedFaceEmbedder());

        var result = await Verify(service, accept, escalate);

        Assert.Equal(BiometricOutcome.Escalate, result.Outcome);
        Assert.Equal(BiometricReasonCodes.ThresholdsInvalid, result.ReasonCode);
        Assert.Equal(0, detector.Calls);
    }

    [Fact]
    public async Task VerifyAsync_PictureThatIsNotAnImage_RejectsAsUnreadable()
    {
        var service = Service(Liveness(live: true, 0.99).Object, new ScriptedFaceDetector(), new ScriptedFaceEmbedder());

        var result = await service.VerifyAsync(new MemoryStream([1, 2, 3]), new MemoryStream([4, 5, 6]), Accept, Escalate);

        Assert.Equal(BiometricOutcome.Reject, result.Outcome);
        Assert.Equal(BiometricReasonCodes.ImageUnreadable, result.ReasonCode);
    }

    [Fact]
    public async Task VerifyAsync_TakesLongerThanTheLimit_EscalatesWithTimeout()
    {
        var detector = new ScriptedFaceDetector { WaitForCancellation = true };
        var service = Service(
            Liveness(live: true, 0.99).Object,
            detector,
            new ScriptedFaceEmbedder(),
            options: new BiometricModelOptions { VerificationTimeoutMs = 100 });

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Escalate, result.Outcome);
        Assert.Equal(BiometricReasonCodes.Timeout, result.ReasonCode);
    }

    [Fact]
    public async Task VerifyAsync_TheCallerCancels_TheCancellationIsNotSwallowed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var service = Service(Liveness(live: true, 0.99).Object, new ScriptedFaceDetector([Face(0.3)], [Face(0.1)]), new ScriptedFaceEmbedder());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyAsync(
            new MemoryStream(TestImages.Png(48, 32, seed: 1)),
            new MemoryStream(TestImages.Png(64, 40, seed: 2)),
            Accept,
            Escalate,
            cancelled.Token));
    }

    [Fact]
    public async Task VerifyAsync_UnexpectedError_EscalatesWithErrorCode()
    {
        var detector = new ScriptedFaceDetector { Failure = new InvalidOperationException("model crashed") };
        var service = Service(Liveness(live: true, 0.99).Object, detector, new ScriptedFaceEmbedder());

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Escalate, result.Outcome);
        Assert.Equal(BiometricReasonCodes.Error, result.ReasonCode);
    }

    [Fact]
    public async Task VerifyAsync_ModelsThatTurnOutUnavailable_EscalateWithUnavailableCode()
    {
        var detector = new ScriptedFaceDetector { Failure = new BiometricUnavailableException() };
        var service = Service(Liveness(live: true, 0.99).Object, detector, new ScriptedFaceEmbedder());

        var result = await Verify(service);

        Assert.Equal(BiometricOutcome.Escalate, result.Outcome);
        Assert.Equal(BiometricReasonCodes.Unavailable, result.ReasonCode);
    }

    [Fact]
    public async Task AntiSpoofing_ModelsNotAvailable_IsNeverLive()
    {
        var service = new AntiSpoofingService(
            NullLogger<AntiSpoofingService>.Instance,
            new FakeBiometricModelStore(available: false),
            new ScriptedFaceDetector(),
            Options.Create(new BiometricModelOptions()));

        var scored = await service.ScoreLivenessAsync(new MemoryStream(TestImages.Png(48, 32, seed: 1)));
        var detected = await service.DetectLivenessAsync(new MemoryStream(TestImages.Png(48, 32, seed: 1)));

        Assert.False(scored.IsLive);
        Assert.Equal(0.0, scored.Score);
        Assert.False(detected);
    }

    [Fact]
    public async Task AntiSpoofing_WithoutAnyDependency_IsNeverLive()
    {
        var service = new AntiSpoofingService(NullLogger<AntiSpoofingService>.Instance);

        Assert.False((await service.ScoreLivenessAsync(new MemoryStream([1, 2, 3]))).IsLive);
    }
}
