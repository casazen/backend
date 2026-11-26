using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

public sealed class ActivePoseVerificationService(
    IFaceDetector detector,
    IFaceEmbedder embedder,
    IOptions<BiometricModelOptions> options,
    ILogger<ActivePoseVerificationService> logger) : IActivePoseVerificationService
{
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
            return double.IsFinite(similarity) && similarity >= acceptThreshold;
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
        // Offset is measured in interocular distances along the eye line, independent of translation, scale and mild roll.
        // Separate neutral/turned ranges and a minimum change avoid classifying small landmark jitter as a head turn.
        var frontal = NoseOffset(first.Landmarks);
        var turned = NoseOffset(second.Landmarks);
        var direction = noseToRight ? 1 : -1;
        return Math.Abs(frontal) <= 0.15 && direction * turned >= 0.22 && direction * (turned - frontal) >= 0.20;
    }

    private static double NoseOffset(float[] points)
    {
        if (points.Length != 10 || !points.All(float.IsFinite))
            return double.NaN;

        double dx = points[2] - points[0], dy = points[3] - points[1];
        var eyeDistanceSquared = dx * dx + dy * dy;
        if (dx <= 0 || eyeDistanceSquared < 64 || Math.Abs(dy) > dx * 0.6)
            return double.NaN;

        var noseX = points[4] - (points[0] + points[2]) * 0.5;
        var noseY = points[5] - (points[1] + points[3]) * 0.5;
        var mouthX = (points[6] + points[8]) * 0.5 - points[4];
        var mouthY = (points[7] + points[9]) * 0.5 - points[5];
        if (dx * noseY <= dy * noseX || dx * mouthY <= dy * mouthX)
            return double.NaN;

        return (noseX * dx + noseY * dy) / eyeDistanceSquared;
    }
}
