namespace Casazen.Core.Services;

public interface IGdprService
{
    Task<Dictionary<string, object>> ExportGuestDataAsync(Guid guestId);
    Task DeleteGuestDataAsync(Guid guestId, string reason);
    Task AnonymizeGuestDataAsync(Guid guestId);
    Task UpdateConsentAsync(Guid guestId, bool marketingConsent);
}
