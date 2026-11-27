namespace Casazen.Core.Services;

public interface IActivePoseVerificationService
{
    Task<bool> VerifyAsync(Stream neutral, Stream turned, bool noseToRight, double acceptThreshold, CancellationToken cancellationToken);
}
