namespace Casazen.Core.Services;

public interface IGdprService
{
    // Guest operations act only on a guest of orgId (TN-1): any other guest, including the guest of
    // another org, raises NotFoundException (guest_not_found).
    Task<Dictionary<string, object>> ExportGuestDataAsync(Guid orgId, Guid guestId);
    Task DeleteGuestDataAsync(Guid orgId, Guid guestId, string reason);
    Task AnonymizeGuestDataAsync(Guid orgId, Guid guestId);
    Task UpdateConsentAsync(Guid orgId, Guid guestId, bool marketingConsent);
    Task<Dictionary<string, object>> ExportOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task AnonymizeOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default);
}
