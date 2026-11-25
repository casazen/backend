using Casazen.Core.Services;

namespace Casazen.Core.GuestPortal;

public interface IPortalMarketingPreferences
{
    Task<GuestMarketingPreferences> GetAsync(PortalContext portal, CancellationToken ct);
    Task<GuestMarketingPreferences> SetAsync(PortalContext portal, GuestMarketingChange change, string? ipAddress, CancellationToken ct);
    Task<MarketingConfirmationSendOutcome> ResendAsync(PortalContext portal, CancellationToken ct);
}
