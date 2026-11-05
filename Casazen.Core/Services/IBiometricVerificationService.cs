using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Face comparison service for biometric check-in verification (ADR-013, NF-11, NF-24).
/// Uses AuraFace-v1 or SFace for face recognition with YuNet for face detection and MiniFASNet for liveness, all in process with ONNX Runtime.
/// All processing is in-memory only with immediate deletion (ADR-013-R2).
/// </summary>
public interface IBiometricVerificationService
{
    /// <summary>
    /// Verifies a selfie against the document photo from the guest's identity document.
    /// Processes in-memory only and deletes all image data before returning (ADR-013-R2).
    /// <para>
    /// The safe default is the operator: when the models are not available (file missing, hash not pinned or different), on a timeout or on an
    /// error the outcome is <see cref="BiometricOutcome.Escalate"/>, never <c>Accept</c>, and the reason is a code in
    /// <see cref="BiometricVerificationResult.ReasonCode"/>.
    /// </para>
    /// </summary>
    /// <param name="selfieStream">The selfie image stream.</param>
    /// <param name="documentPhotoStream">The document photo image stream (from identity document).</param>
    /// <param name="acceptThreshold">Accept threshold (e.g., 0.85). Similarity >= this is Accept.</param>
    /// <param name="escalateThreshold">Escalate threshold (e.g., 0.70). Similarity above this and below acceptThreshold is Escalate; at or under it is Reject (ADR-013-R4).</param>
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
/// Result of biometric verification with model metadata for audit logging. Numbers, flags and codes only: never an image, a crop or a vector
/// (ADR-013-R2, AC-NF51-8).
/// </summary>
public class BiometricVerificationResult
{
    /// <summary>The outcome: Accept, Escalate, or Reject.</summary>
    public BiometricOutcome Outcome { get; set; }

    /// <summary>Cosine similarity (0.0 to 1.0, negative values are 0) between the selfie and the document photo; 0 when no comparison was made.</summary>
    public double Similarity { get; set; }

    /// <summary>Name of the face recognition model used (e.g., "AuraFace-v1", "SFace").</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Version of the model used (e.g., "1.0.0").</summary>
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>True if the anti-spoofing check detected a live selfie, false if likely spoofed or not run.</summary>
    public bool IsLive { get; set; }

    /// <summary>Score of the real-face class of the anti-spoofing model (0 to 1); 0 when the model did not run.</summary>
    public double LivenessScore { get; set; }

    /// <summary>True when a face was found in the selfie.</summary>
    public bool FaceInSelfie { get; set; }

    /// <summary>True when a face was found in the photo of the document.</summary>
    public bool FaceInDocument { get; set; }

    /// <summary>Share of the selfie covered by the box of its face (0 to 1); 0 when no face was found.</summary>
    public double FaceAreaRatioSelfie { get; set; }

    /// <summary>Time the verification took, in milliseconds.</summary>
    public int DurationMs { get; set; }

    /// <summary>
    /// Why the outcome is not an <c>Accept</c> that follows from the similarity, a code from <see cref="BiometricReasonCodes"/>; null when the
    /// outcome comes from the comparison alone.
    /// </summary>
    public string? ReasonCode { get; set; }
}

/// <summary>Reason codes of a verification result (codes only, never a text with data in it).</summary>
public static class BiometricReasonCodes
{
    /// <summary>No face in the selfie or in the photo of the document: <c>Reject</c>.</summary>
    public const string NoFace = "no_face";

    /// <summary>More than one face in the selfie: <c>Reject</c>.</summary>
    public const string MultipleFaces = "multiple_faces";

    /// <summary>The face of the selfie covers less than <c>Biometrics:MinFaceAreaRatio</c> of the picture: <c>Reject</c>.</summary>
    public const string FaceTooSmall = "face_too_small";

    /// <summary>The anti-spoofing model does not judge the selfie live: <c>Reject</c>.</summary>
    public const string NotLive = "not_live";

    /// <summary>A picture could not be decoded or is too large: <c>Reject</c>.</summary>
    public const string ImageUnreadable = "image_unreadable";

    /// <summary>The models are not available (not loaded, file missing, hash not pinned or different): <c>Escalate</c>, the operator decides.</summary>
    public const string Unavailable = "biometrics_unavailable";

    /// <summary>The verification took longer than <c>Biometrics:VerificationTimeoutMs</c>: <c>Escalate</c>.</summary>
    public const string Timeout = "timeout";

    /// <summary>The verification failed unexpectedly: <c>Escalate</c>.</summary>
    public const string Error = "error";

    /// <summary>The thresholds received are not usable (the accept threshold must be above the escalate one, both between 0 and 1): <c>Escalate</c>.</summary>
    public const string ThresholdsInvalid = "thresholds_invalid";
}
