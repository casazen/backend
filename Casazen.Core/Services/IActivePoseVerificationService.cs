namespace Casazen.Core.Services;

/// <summary>Checks that the same face turns from a frontal pose in the requested direction in image coordinates.</summary>
public interface IActivePoseVerificationService
{
    Task<bool> VerifyAsync(Stream neutral, Stream turned, bool noseToRight, double acceptThreshold, CancellationToken cancellationToken);
}
