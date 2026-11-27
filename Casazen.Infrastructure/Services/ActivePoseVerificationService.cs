using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public sealed class ActivePoseVerificationService(
    IFaceDetector detector,
    IFaceEmbedder embedder,
    IOptions<BiometricModelOptions> options,
    ILogger<ActivePoseVerificationService> logger) : IActivePoseVerificationService
{
    // OpenCV Zoo's SFace reference publishes this cosine threshold for same-person comparisons.
    // Bind it to the exact model and preprocessing; replacement models retain the caller's threshold.
    private const string SFace2021DecSha256 = "0BA9FBFA01B5270C96627C4EF784DA859931E02F04419C829E83484087C34E79";
    private const double SFacePoseSimilarity = 0.363;

    public async Task<bool> VerifyAsync(Stream neutral, Stream turned, bool noseToRight, double acceptThreshold,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!double.IsFinite(acceptThreshold) || acceptThreshold <= 0 || acceptThreshold > 1)
            return false;

        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.VerificationTimeoutMs);
        var token = timeout.Token;
        float[]? neutralVector = null;
        float[]? turnedVector = null;
        try
        {
            using var first = await DecodeAsync(neutral, settings, token);
            using var second = await DecodeAsync(turned, settings, token);
            if (first.Width == second.Width && first.Height == second.Height && first.Rgb.AsSpan().SequenceEqual(second.Rgb))
                return false;

            var frontalFace = FindFace(first, settings, token);
            var turnedFace = FindFace(second, settings, token);
            if (frontalFace is null || turnedFace is null || !IsRequestedTurn(frontalFace, turnedFace, noseToRight))
                return false;

            neutralVector = embedder.Embed(first, frontalFace, token);
            turnedVector = embedder.Embed(second, turnedFace, token);
            var similarity = EmbeddingMath.CosineSimilarity(neutralVector, turnedVector);
            token.ThrowIfCancellationRequested();
            return double.IsFinite(similarity) && similarity >= PoseSimilarityThreshold(settings, acceptThreshold);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Active pose could not be verified ({ErrorType})", ex.GetType().Name);
            return false;
        }
        finally
        {
            ImageBytes.Zero(neutralVector);
            ImageBytes.Zero(turnedVector);
        }
    }

    internal static double PoseSimilarityThreshold(BiometricModelOptions settings, double fallback) =>
        string.Equals(settings.FaceRecognition.Sha256, SFace2021DecSha256, StringComparison.OrdinalIgnoreCase) &&
        settings.RecognitionMean == 0 && settings.RecognitionStd == 1 && settings.RecognitionRgbOrder
            ? SFacePoseSimilarity
            : fallback;

    private static async Task<RasterImage> DecodeAsync(Stream stream, BiometricModelOptions settings, CancellationToken token)
    {
        var bytes = await ImageBytes.ReadAllAsync(stream, settings.MaxImageBytes, token);
        try
        {
            return RasterImage.Decode(bytes, settings.MaxImagePixels);
        }
        finally
        {
            ImageBytes.Zero(bytes);
        }
    }

    private DetectedFace? FindFace(RasterImage image, BiometricModelOptions settings, CancellationToken token)
    {
        var faces = detector.Detect(image, settings.DetectionScoreThreshold, token);
        return faces.Count == 1 && CaptureQualityAssessor.AssessSelfie(image, faces, settings).Acceptable ? faces[0] : null;
    }

    internal static bool IsRequestedTurn(DetectedFace first, DetectedFace second, bool noseToRight)
    {
        // The weak-perspective estimate is a guide to a deliberate turn, not a calibrated anatomical measurement.
        var frontal = FacePose.YawDegrees(first.Landmarks);
        var turned = FacePose.YawDegrees(second.Landmarks);
        var direction = noseToRight ? 1 : -1;
        return Math.Abs(frontal) <= 15 && direction * turned >= 20 && direction * (turned - frontal) >= 15;
    }
}
