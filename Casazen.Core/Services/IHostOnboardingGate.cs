using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

public interface IHostOnboardingGate
{
    Task<HostOnboardingStatus> GetStatusAsync(string userId, CancellationToken cancellationToken = default);
}
