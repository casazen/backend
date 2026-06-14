using Casazen.Core.Models;

namespace Casazen.Core.Services;

public interface IOnboardingService
{
    Task<(bool Success, ConsentValidationError? Error, bool ConsentsRecorded)> ValidateAndRecordConsentsAsync(
        string userId,
        Guid orgId,
        OnboardingConsentsInput? consents,
        bool requireConsents,
        string? clientIpAddress,
        CancellationToken cancellationToken);

    Task<OnboardingActivationStatus> GetActivationStatusAsync(string userId, CancellationToken cancellationToken);
}
