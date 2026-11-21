namespace Casazen.Core.GuestPortal;

/// <summary>
/// A rule of the host on what the guest portal shows (E50/F01/US01/T02, decision A4): every registered policy must agree, so a single «no» hides the
/// paid services (the host's extras and the services of the suppliers) for the stay. <see cref="DefaultGuestPortalPolicy"/> shows them (D12: the portal
/// shows everything by default); the switch that hides them on the Airbnb bookings (D39) is the policy of the portal configuration (E59). A policy that
/// throws counts as a «no».
/// </summary>
[GuestPortalRegistry]
public interface IGuestPortalPolicy
{
    /// <summary>Whether the paid services may be shown for the stay of <paramref name="context"/>.</summary>
    Task<bool> CanShowPaidServicesAsync(PortalContext context, CancellationToken cancellationToken = default);
}

/// <summary>The default: the paid services are shown, for every stay and every source (D12).</summary>
public sealed class DefaultGuestPortalPolicy : IGuestPortalPolicy
{
    public Task<bool> CanShowPaidServicesAsync(PortalContext context, CancellationToken cancellationToken = default) => Task.FromResult(true);
}
