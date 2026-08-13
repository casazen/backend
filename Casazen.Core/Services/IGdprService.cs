namespace Casazen.Core.Services;

public interface IGdprService
{
    Task<Dictionary<string, object>> ExportGuestDataAsync(Guid guestId);
    Task DeleteGuestDataAsync(Guid guestId, string reason);
    Task AnonymizeGuestDataAsync(Guid guestId);
    Task UpdateConsentAsync(Guid guestId, bool marketingConsent);
    Task<Dictionary<string, object>> ExportOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default);
    Task AnonymizeOrgFiscalDataAsync(Guid orgId, CancellationToken cancellationToken = default);
}
