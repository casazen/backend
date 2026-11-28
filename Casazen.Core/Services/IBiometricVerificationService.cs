using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

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

public class BiometricVerificationResult
{
    public BiometricOutcome Outcome { get; set; }

    public double Similarity { get; set; }

    public string ModelName { get; set; } = string.Empty;

    public string ModelVersion { get; set; } = string.Empty;

    public bool IsLive { get; set; }

    public double LivenessScore { get; set; }

    public bool FaceInSelfie { get; set; }

    public bool FaceInDocument { get; set; }

    public double FaceAreaRatioSelfie { get; set; }

    public int DurationMs { get; set; }

    public string? ReasonCode { get; set; }
}

public static class BiometricReasonCodes
{
    public const string NoFace = "no_face";

    public const string MultipleFaces = "multiple_faces";

    public const string FaceTooSmall = "face_too_small";

    public const string NotLive = "not_live";

    public const string ImageUnreadable = "image_unreadable";

    public const string Unavailable = "biometrics_unavailable";

    public const string Timeout = "timeout";

    public const string Error = "error";

    public const string ThresholdsInvalid = "thresholds_invalid";
}
