using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;

namespace Casazen.Web.DTOs.GuestPortal;

public sealed class GuestPortalLinkDto
{
    public string State { get; init; } = GuestPortalRules.StateNone;

    /// <summary>
    /// The address to copy (<c>/s/{token}</c> on the CasaZen domain), only while the link is active and only for a caller who may write the booking:
    /// the address opens the guest portal of the stay. <c>null</c> otherwise.
    /// </summary>
    public string? Url { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? ExpiresAt { get; init; }

    public bool Eligible { get; init; }

    public string? IneligibleReason { get; init; }

    public static GuestPortalLinkDto From(GuestPortalLink? link, Booking booking, DateTime nowUtc, Func<GuestPortalLink, string?> url)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(url);

        var reason = GuestPortalRules.IneligibleReason(booking, nowUtc);
        if (link is null)
            return new GuestPortalLinkDto { Eligible = reason is null, IneligibleReason = reason };

        var expiry = GuestPortalRules.EffectiveExpiry(link.ExpiresAt, booking.CheckOutDate);
        var active = expiry > nowUtc;
        return new GuestPortalLinkDto
        {
            State = active ? GuestPortalRules.StateActive : GuestPortalRules.StateExpired,
            Url = active ? url(link) : null,
            CreatedAt = link.CreatedAt,
            ExpiresAt = expiry,
            Eligible = reason is null,
            IneligibleReason = reason,
        };
    }
}
