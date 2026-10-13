using Casazen.Core.Models;

namespace Casazen.Core.Services;

public interface IOnboardingService
{
    (bool Success, ConsentValidationError? Error) ValidateConsents(
        OnboardingConsentsInput? consents,
        bool requireConsents);

    Task<(bool Success, ConsentValidationError? Error, bool ConsentsRecorded)> ValidateAndRecordConsentsAsync(
        string userId,
        Guid orgId,
        OnboardingConsentsInput? consents,
        bool requireConsents,
        string? clientIpAddress,
        CancellationToken cancellationToken);

    Task<OnboardingActivationStatus> GetActivationStatusAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// True when the org has at least one property (any rental mode). The tenant filter is lifted and the org is scoped
    /// explicitly; the soft-delete filter stays, so a deleted property does not count. Drives the "first property
    /// pending" state of <c>GET /api/users/me</c>.
    /// </summary>
    Task<bool> OrgHasPropertiesAsync(Guid orgId, CancellationToken cancellationToken);
}
