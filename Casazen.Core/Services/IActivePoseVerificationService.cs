namespace Casazen.Core.Services;

/// <summary>Checks that the same face turns from a frontal pose in the requested direction in image coordinates.</summary>
public interface IActivePoseVerificationService
{
    /// <summary>The model-specific cross-pose criterion falls back to <paramref name="acceptThreshold"/> for other models.
    /// The neutral selfie is verified against the document separately with the organisation's configured thresholds.</summary>
    Task<bool> VerifyAsync(Stream neutral, Stream turned, bool noseToRight, double acceptThreshold, CancellationToken cancellationToken);
}
