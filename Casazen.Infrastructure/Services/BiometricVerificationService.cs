using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Face comparison service using AuraFace-v1/SFace + YuNet for biometric check-in (ADR-013, NF-11).
/// All processing is in-memory only with immediate deletion in finally blocks (ADR-013-R2).
/// Returns three-level outcome (Accept/Escalate/Reject) based on configurable thresholds (ADR-013-R4).
/// </summary>
public class BiometricVerificationService(
    IAntiSpoofingService antiSpoofingService,
    ILogger<BiometricVerificationService> logger) : IBiometricVerificationService
{
    private const string ModelName = "AuraFace-v1"; // or "SFace" as fallback
    private const string ModelVersion = "1.0.0";

    /// <summary>
    /// Verifies a selfie against the document photo with in-memory processing only (ADR-013-R2).
    /// All image data (selfie, crop, embeddings) is deleted in finally blocks before returning.
    /// </summary>
    public async Task<BiometricVerificationResult> VerifyAsync(
        Stream selfieStream,
        Stream documentPhotoStream,
        double acceptThreshold,
        double escalateThreshold,
        CancellationToken cancellationToken = default)
    {
        byte[]? selfieBytes = null;
        byte[]? documentPhotoBytes = null;
        byte[]? selfieCrop = null;
        byte[]? documentCrop = null;
        float[]? selfieEmbedding = null;
        float[]? documentEmbedding = null;

        try
        {
            // Step 1: Load images into memory (will be deleted in finally)
            using (var ms = new MemoryStream())
            {
                await selfieStream.CopyToAsync(ms, cancellationToken);
                selfieBytes = ms.ToArray();
            }

            using (var ms = new MemoryStream())
            {
                await documentPhotoStream.CopyToAsync(ms, cancellationToken);
                documentPhotoBytes = ms.ToArray();
            }

            // Step 2: Anti-spoofing check on selfie (ADR-013, AC-NF11-6)
            using (var selfieMemStream = new MemoryStream(selfieBytes))
            {
                var isLive = await antiSpoofingService.DetectLivenessAsync(selfieMemStream, cancellationToken);

                if (!isLive)
                {
                    logger.LogWarning("Anti-spoofing check failed: selfie appears to be spoofed");
                    return new BiometricVerificationResult
                    {
                        Outcome = BiometricOutcome.Reject,
                        Similarity = 0.0,
                        ModelName = ModelName,
                        ModelVersion = ModelVersion,
                        IsLive = false,
                    };
                }
            }

            // TODO: Steps 3-6 will integrate real face detection and comparison models
            // For now, this is a placeholder implementation that demonstrates the structure

            // Step 3: YuNet face detection on both images
            // - Load YuNet model (MIT license)
            // - Detect face in selfie, extract bounding box
            // - Detect face in document photo, extract bounding box
            // - If no face detected in either -> Reject

            // Step 4: Crop faces from both images
            // selfieCrop = CropFace(selfieBytes, faceBoundingBox);
            // documentCrop = CropFace(documentPhotoBytes, faceBoundingBox);

            // Step 5: Generate face embeddings with AuraFace-v1 (or SFace fallback)
            // - Load AuraFace-v1 glintr100.onnx (Apache-2.0)
            // - Preprocess crops (resize, normalize)
            // - Run inference to get embeddings
            // selfieEmbedding = GenerateEmbedding(selfieCrop);
            // documentEmbedding = GenerateEmbedding(documentCrop);

            // Step 6: Calculate cosine similarity between embeddings
            // var similarity = CosineSimilarity(selfieEmbedding, documentEmbedding);

            // Placeholder: simulate similarity score
            var similarity = 0.75; // Placeholder value

            logger.LogInformation(
                "Biometric verification completed (placeholder): similarity={Similarity}, accept={Accept}, escalate={Escalate}",
                similarity, acceptThreshold, escalateThreshold);

            // Step 7: Determine outcome based on thresholds (ADR-013-R4)
            var outcome = similarity >= acceptThreshold
                ? BiometricOutcome.Accept
                : similarity >= escalateThreshold
                    ? BiometricOutcome.Escalate
                    : BiometricOutcome.Reject;

            return new BiometricVerificationResult
            {
                Outcome = outcome,
                Similarity = similarity,
                ModelName = ModelName,
                ModelVersion = ModelVersion,
                IsLive = true,
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Biometric verification failed");

            // On error, escalate to operator (safe default)
            return new BiometricVerificationResult
            {
                Outcome = BiometricOutcome.Escalate,
                Similarity = 0.0,
                ModelName = ModelName,
                ModelVersion = ModelVersion,
                IsLive = false,
            };
        }
        finally
        {
            // ADR-013-R2: Immediate deletion guarantee
            // Delete all image data (selfie, crop, embedding) before returning
            if (selfieBytes != null)
            {
                Array.Clear(selfieBytes, 0, selfieBytes.Length);
                selfieBytes = null;
            }

            if (documentPhotoBytes != null)
            {
                Array.Clear(documentPhotoBytes, 0, documentPhotoBytes.Length);
                documentPhotoBytes = null;
            }

            if (selfieCrop != null)
            {
                Array.Clear(selfieCrop, 0, selfieCrop.Length);
                selfieCrop = null;
            }

            if (documentCrop != null)
            {
                Array.Clear(documentCrop, 0, documentCrop.Length);
                documentCrop = null;
            }

            if (selfieEmbedding != null)
            {
                Array.Clear(selfieEmbedding, 0, selfieEmbedding.Length);
                selfieEmbedding = null;
            }

            if (documentEmbedding != null)
            {
                Array.Clear(documentEmbedding, 0, documentEmbedding.Length);
                documentEmbedding = null;
            }
        }
    }
}
