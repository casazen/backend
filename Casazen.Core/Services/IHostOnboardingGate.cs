using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// Reads the host onboarding status of a user (PL-02): onboarding completed and current consents accepted
/// (<see cref="HostOnboarding"/>). Shares the per-request / short-lived authorization cache of the context policies,
/// so asking it in a handler or a controller does not hit the database again.
/// </summary>
public interface IHostOnboardingGate
{
    Task<HostOnboardingStatus> GetStatusAsync(string userId, CancellationToken cancellationToken = default);
}
