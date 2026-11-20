using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;

namespace Casazen.Web.DTOs.GuestPortal;

/// <summary>
/// The stay link of a booking as the host sees it (E50/F01/US01/T01, NF-152): <c>GET</c>, <c>POST</c> and <c>POST regenerate</c> of
/// <c>api/bookings/{bookingId}/guest-portal</c>. <c>{ state, url, createdAt, expiresAt, eligible, ineligibleReason }</c>, camelCase.
/// </summary>
public sealed class GuestPortalLinkDto
{
    /// <summary><c>none</c> (no link, or only revoked ones), <c>active</c> (the link opens) or <c>expired</c> (the stay ended: it no longer opens).</summary>
    public string State { get; init; } = GuestPortalRules.StateNone;

    /// <summary>
    /// The address to copy (<c>/s/{token}</c> on the CasaZen domain), only while the link is active and only for a caller who may write the booking:
    /// the address opens the guest portal of the stay. <c>null</c> otherwise.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>When the active link was made (UTC); <c>null</c> without one.</summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>When the active link stops opening (UTC): the end of the 30th day after the departure; <c>null</c> without one.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Whether the booking can get a link now (a confirmed, checked-in or checked-out stay of any source, not over by 30 days).</summary>
    public bool Eligible { get; init; }

    /// <summary><c>null</c>, <c>booking_status</c> (a request, cancelled, a no-show) or <c>stay_ended</c>.</summary>
    public string? IneligibleReason { get; init; }

    /// <summary>
    /// The view of <paramref name="link"/> (the active one of <paramref name="booking"/>, or <c>null</c>) at <paramref name="nowUtc"/>. The expiry is the
    /// effective one (a departure moved later extends it). <paramref name="url"/> is used only for an active link.
    /// </summary>
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
