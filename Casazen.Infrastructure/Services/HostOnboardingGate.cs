using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Host onboarding gate (PL-02, A1-05), see <see cref="HostOnboarding"/>. The status comes from the cached
/// <see cref="UserAuthorizationSnapshot"/>; the current document versions from <see cref="ILegalDocumentService"/>,
/// read at every evaluation so that publishing a new version asks every host to accept it again.
/// </summary>
/// <remarks>
/// The gate requires the Terms of Service, the Privacy notice and the DPA. The subprocessors acknowledgement is still
/// collected by the onboarding but does not block the host contexts: its version changes on its own when an AI provider
/// is switched on (<c>LegalDocumentService.GetSubprocessors</c>).
/// </remarks>
public sealed class HostOnboardingGate(
    IUserAuthorizationSnapshotStore snapshotStore,
    ILegalDocumentService legalDocuments) : IHostOnboardingGate
{
    public async Task<HostOnboardingStatus> GetStatusAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return HostOnboardingStatus.NotStarted;

        var snapshot = await snapshotStore.GetAsync(userId, cancellationToken);
        return Evaluate(snapshot, legalDocuments);
    }

    /// <summary>The gate's decision for an already loaded snapshot.</summary>
    public static HostOnboardingStatus Evaluate(UserAuthorizationSnapshot snapshot, ILegalDocumentService legalDocuments)
    {
        if (!snapshot.Exists)
            return HostOnboardingStatus.NotStarted;

        var consentsAccepted = snapshot.OrgId is not null
            && snapshot.HasAccepted(ConsentType.Tos, legalDocuments.GetTos().Version)
            && snapshot.HasAccepted(ConsentType.Privacy, legalDocuments.GetPrivacy().Version)
            && snapshot.HasAccepted(ConsentType.Dpa, legalDocuments.GetDpa().Version);

        return new HostOnboardingStatus(snapshot.OnboardingCompletedAt is not null, consentsAccepted);
    }
}
