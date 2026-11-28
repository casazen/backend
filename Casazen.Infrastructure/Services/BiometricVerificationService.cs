using System.Diagnostics;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Face comparison service using AuraFace-v1/SFace + YuNet + MiniFASNet for biometric check-in (ADR-013, NF-11, NF-24, NF-51).
/// All processing is in-memory only with immediate deletion in finally blocks (ADR-013-R2). Runs synchronously inside the request and stops after
/// <c>Biometrics:VerificationTimeoutMs</c> (ADR-013-R1). Returns the three-level outcome (Accept/Escalate/Reject) from the thresholds received
/// (ADR-013-R4). <b>The safe default is the operator</b>: models not available, a timeout or an error are <c>Escalate</c>, never a match.
/// </summary>
public class BiometricVerificationService(
    IAntiSpoofingService antiSpoofingService,
    ILogger<BiometricVerificationService> logger,
    IBiometricModelStore? modelStore = null,
    IFaceDetector? faceDetector = null,
    IFaceEmbedder? faceEmbedder = null,
    IOptions<BiometricModelOptions>? options = null) : IBiometricVerificationService
{
    public async Task<BiometricVerificationResult> VerifyAsync(
        Stream selfieStream,
        Stream documentPhotoStream,
        double acceptThreshold,
        double escalateThreshold,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = options?.Value ?? new BiometricModelOptions();
        var result = new BiometricVerificationResult
        {
            ModelName = string.IsNullOrWhiteSpace(settings.FaceRecognition.Name) ? "unknown" : settings.FaceRecognition.Name,
            ModelVersion = string.IsNullOrWhiteSpace(settings.FaceRecognition.Version) ? "unknown" : settings.FaceRecognition.Version,
        };

        byte[]? selfieBytes = null;
        byte[]? documentBytes = null;
        RasterImage? selfie = null;
        RasterImage? document = null;
        RasterImage? rotatedDocument = null;
        float[]? selfieVector = null;
        float[]? documentVector = null;
        try
        {
            if (!(acceptThreshold > escalateThreshold) || escalateThreshold < 0 || acceptThreshold > 1)
                return Finish(result, BiometricOutcome.Escalate, BiometricReasonCodes.ThresholdsInvalid, started);

            if (modelStore is not { IsAvailable: true } || faceDetector is null || faceEmbedder is null)
            {
                logger.LogInformation("Biometric verification goes to the operator: the models are not available");
                return Finish(result, BiometricOutcome.Escalate, BiometricReasonCodes.Unavailable, started);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(settings.VerificationTimeoutMs);
            var token = timeout.Token;

            selfieBytes = await ImageBytes.ReadAllAsync(selfieStream, settings.MaxImageBytes, token);
            documentBytes = await ImageBytes.ReadAllAsync(documentPhotoStream, settings.MaxImageBytes, token);
            try
            {
                selfie = RasterImage.Decode(selfieBytes, settings.MaxImagePixels);
                document = RasterImage.Decode(documentBytes, settings.MaxImagePixels);
            }
            catch (InvalidDataException)
            {
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.ImageUnreadable, started);
            }

            ImageBytes.Zero(documentBytes);

            var selfieFaces = faceDetector.Detect(selfie, settings.DetectionScoreThreshold, token);
            var portrait = DocumentPortrait.Find(faceDetector, document, settings, token, out rotatedDocument);
            result.FaceInSelfie = selfieFaces.Count > 0;
            result.FaceInDocument = portrait is not null;
            if (!result.FaceInSelfie || portrait is null)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.NoFace, started);

            result.FaceAreaRatioSelfie = selfieFaces[0].AreaRatio;
            if (selfieFaces.Count > 1)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.MultipleFaces, started);

            if (selfieFaces[0].AreaRatio < settings.MinFaceAreaRatio)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.FaceTooSmall, started);

            LivenessResult liveness;
            using (var selfieCopy = new MemoryStream(selfieBytes, writable: false))
                liveness = await antiSpoofingService.ScoreLivenessAsync(selfieCopy, token);

            ImageBytes.Zero(selfieBytes);
            result.IsLive = liveness.IsLive;
            result.LivenessScore = liveness.Score;
            if (!liveness.IsLive)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.NotLive, started);

            selfieVector = faceEmbedder.Embed(selfie, selfieFaces[0], token);
            documentVector = faceEmbedder.Embed(rotatedDocument ?? document, portrait, token);
            result.Similarity = Math.Clamp(EmbeddingMath.CosineSimilarity(selfieVector, documentVector), 0.0, 1.0);

            return Finish(result, MapOutcome(result.Similarity, acceptThreshold, escalateThreshold), null, started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Biometric verification timed out; it goes to the operator");
            return Finish(result, BiometricOutcome.Escalate, BiometricReasonCodes.Timeout, started);
        }
        catch (BiometricUnavailableException)
        {
            logger.LogWarning("Biometric verification goes to the operator: the models are not available");
            return Finish(result, BiometricOutcome.Escalate, BiometricReasonCodes.Unavailable, started);
        }
        catch (Exception ex)
        {
            logger.LogError("Biometric verification failed ({ErrorType}); it goes to the operator", ex.GetType().Name);
            return Finish(result, BiometricOutcome.Escalate, BiometricReasonCodes.Error, started);
        }
        finally
        {
            ImageBytes.Zero(selfieBytes);
            ImageBytes.Zero(documentBytes);
            ImageBytes.Zero(selfieVector);
            ImageBytes.Zero(documentVector);
            selfie?.Dispose();
            document?.Dispose();
            rotatedDocument?.Dispose();
        }
    }

    internal static BiometricOutcome MapOutcome(double similarity, double acceptThreshold, double escalateThreshold) =>
        similarity >= acceptThreshold
            ? BiometricOutcome.Accept
            : similarity > escalateThreshold
                ? BiometricOutcome.Escalate
                : BiometricOutcome.Reject;

    private BiometricVerificationResult Finish(BiometricVerificationResult result, BiometricOutcome outcome, string? reason, long started)
    {
        result.Outcome = outcome;
        result.ReasonCode = reason;
        result.DurationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Biometric verification: outcome={Outcome} reason={Reason} similarity={Similarity:F3} liveness={Liveness:F3} faceInSelfie={FaceInSelfie} faceInDocument={FaceInDocument} durationMs={DurationMs}",
            outcome, reason ?? "none", result.Similarity, result.LivenessScore, result.FaceInSelfie, result.FaceInDocument, result.DurationMs);
        return result;
    }
}
