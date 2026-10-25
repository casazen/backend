using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Face comparison service for biometric check-in verification (ADR-013, NF-11).
/// Uses AuraFace-v1 or SFace for face recognition with YuNet for face detection.
/// All processing is in-memory only with immediate deletion (ADR-013-R2).
/// </summary>
public interface IBiometricVerificationService
{
    /// <summary>
    /// Verifies a selfie against the document photo from the guest's identity document.
    /// Processes in-memory only and deletes all image data before returning (ADR-013-R2).
    /// </summary>
    /// <param name="selfieStream">The selfie image stream.</param>
    /// <param name="documentPhotoStream">The document photo image stream (from identity document).</param>
    /// <param name="acceptThreshold">Accept threshold (e.g., 0.85). Similarity >= this is Accept.</param>
    /// <param name="escalateThreshold">Escalate threshold (e.g., 0.70). Similarity < acceptThreshold and >= this is Escalate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Biometric outcome based on face comparison similarity and thresholds.</returns>
    Task<BiometricVerificationResult> VerifyAsync(
        Stream selfieStream,
        Stream documentPhotoStream,
        double acceptThreshold,
        double escalateThreshold,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of biometric verification with model metadata for audit logging.
/// </summary>
public class BiometricVerificationResult
{
    /// <summary>The outcome: Accept, Escalate, or Reject.</summary>
    public BiometricOutcome Outcome { get; set; }

    /// <summary>Similarity score (0.0 to 1.0) between the selfie and document photo.</summary>
    public double Similarity { get; set; }

    /// <summary>Name of the face recognition model used (e.g., "AuraFace-v1", "SFace").</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Version of the model used (e.g., "1.0.0").</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>True if the anti-spoofing check detected a live selfie, false if likely spoofed.</summary>
    public bool IsLive { get; set; }
}
