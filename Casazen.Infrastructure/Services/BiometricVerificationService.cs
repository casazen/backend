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
    /// <summary>
    /// Verifies a selfie against the document photo with in-memory processing only (ADR-013-R2): the pictures are decoded, the faces found and
    /// compared, and every picture, crop and vector is zeroed in the finally block, on success, failure and timeout.
    /// </summary>
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

            // Step 1: the two pictures into memory (they are zeroed in the finally block).
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

            // Step 2: YuNet on both pictures. No face in either one is a Reject; the selfie must hold one face, large enough.
            var selfieFaces = faceDetector.Detect(selfie, settings.DetectionScoreThreshold, token);
            var documentFaces = faceDetector.Detect(document, settings.DocumentDetectionScoreThreshold, token);
            result.FaceInSelfie = selfieFaces.Count > 0;
            result.FaceInDocument = documentFaces.Count > 0;
            if (!result.FaceInSelfie || !result.FaceInDocument)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.NoFace, started);

            result.FaceAreaRatioSelfie = selfieFaces[0].AreaRatio;
            if (selfieFaces.Count > 1)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.MultipleFaces, started);

            if (selfieFaces[0].AreaRatio < settings.MinFaceAreaRatio)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.FaceTooSmall, started);

            // Step 3: anti-spoofing on the selfie (AC-NF11-6): the model runs, a score under the threshold is not live.
            LivenessResult liveness;
            using (var selfieCopy = new MemoryStream(selfieBytes, writable: false))
                liveness = await antiSpoofingService.ScoreLivenessAsync(selfieCopy, token);

            ImageBytes.Zero(selfieBytes);
            result.IsLive = liveness.IsLive;
            result.LivenessScore = liveness.Score;
            if (!liveness.IsLive)
                return Finish(result, BiometricOutcome.Reject, BiometricReasonCodes.NotLive, started);

            // Step 4: crop, align, normalise, embed both faces and compare them. The document photo is the largest face of its picture.
            selfieVector = faceEmbedder.Embed(selfie, selfieFaces[0], token);
            documentVector = faceEmbedder.Embed(document, documentFaces[0], token);
            result.Similarity = Math.Clamp(EmbeddingMath.CosineSimilarity(selfieVector, documentVector), 0.0, 1.0);

            // Step 5: the outcome of the thresholds (ADR-013-R4).
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
            // On error, escalate to operator (safe default). The type only: a message could carry what was being read.
            logger.LogError("Biometric verification failed ({ErrorType}); it goes to the operator", ex.GetType().Name);
            return Finish(result, BiometricOutcome.Escalate, BiometricReasonCodes.Error, started);
        }
        finally
        {
            // ADR-013-R2: Immediate deletion guarantee
            // Delete all image data (selfie, document photo, crops, embeddings) before returning, on success, failure and timeout.
            ImageBytes.Zero(selfieBytes);
            ImageBytes.Zero(documentBytes);
            ImageBytes.Zero(selfieVector);
            ImageBytes.Zero(documentVector);
            selfie?.Dispose();
            document?.Dispose();
        }
    }

    /// <summary>
    /// ADR-013-R4: Accept at or above the accept threshold, Reject at or under the escalate threshold, Escalate between the two.
    /// </summary>
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
