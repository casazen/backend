using Casazen.Core.Entities;

namespace Casazen.Core.GuestPortal;

/// <summary>
/// What a request of the guest portal is about (E50/F01/US01/T02, NF-152, NF-153, decision A3): the stay the token of the link opens, resolved once
/// per request by the token filter of the portal and handed to every section. It is the <b>only</b> source of the booking, the org and the property of
/// a portal endpoint: no endpoint of the portal reads a booking id from the client, and every query of a section names <see cref="OrgId"/> and
/// <see cref="BookingId"/> (or <see cref="PropertyId"/>) explicitly. The tenant of a portal request is the token, whoever is signed in.
/// </summary>
/// <remarks>
/// Made only by <c>PortalContextFactory</c> (Infrastructure), which is also the only place that sets <see cref="CanSeeReserved"/>: the check-in of
/// the portal (E51) refines it there. Keys are only ever added.
/// </remarks>
public sealed record PortalContext
{
    /// <summary>The stay link the token opened: the id the logs carry, never the token.</summary>
    public required Guid LinkId { get; init; }

    public required Guid OrgId { get; init; }

    public required Guid BookingId { get; init; }

    public required Guid PropertyId { get; init; }

    /// <summary>
    /// The language of the guest, one of <c>GuestLanguages.Supported</c> (it, en, de, fr, es): the one the guest chose on the latest check-in session
    /// that has one, then the default language of the org, then Italian (<c>GuestLanguages.Resolve</c>).
    /// </summary>
    public required string Language { get; init; }

    /// <summary>The status of the booking: confirmed, checked in, checked out or cancelled (a request never opens the portal).</summary>
    public required BookingStatus Status { get; init; }

    public required BookingSource Source { get; init; }

    /// <summary>A booking of a portal (Airbnb, Booking.com, another channel): the one definition of the code, <c>FiscalCopy.IsOtaBookingSource</c>.</summary>
    public required bool IsOta { get; init; }

    /// <summary>A booking of the booking site of the host (<see cref="BookingSource.Direct"/>): the only one the guest may change or cancel (D4).</summary>
    public required bool IsDirect { get; init; }

    /// <summary>A check-in session of the booking is completed (<c>GuestCheckInSession.IsCompleted</c>).</summary>
    public required bool CheckInCompleted { get; init; }

    /// <summary>
    /// Whether the portal may show the reserved data of the stay (the precise address, the access instructions, the door code, the Wi-Fi): only
    /// after the check-in is completed (§5.1 of the specification, AC-NF153-3) and only for a stay to come or in progress, a confirmed or checked-in
    /// booking that is not a no-show: a cancelled stay, a no-show and a stay already checked out keep their link but never get it, even with a completed
    /// check-in (<see cref="GuestPortalRules.IsStayOpenToReservedData"/>, the rule of the lock code). Set only by <c>PortalContextFactory</c>.
    /// </summary>
    public required bool CanSeeReserved { get; init; }

    /// <summary>The arrival day (UTC midnight, as stored on the booking).</summary>
    public required DateTime CheckInDate { get; init; }

    /// <summary>The departure day (UTC midnight, as stored on the booking).</summary>
    public required DateTime CheckOutDate { get; init; }
}

/// <summary>
/// The portal context of the current request (<see cref="PortalContext"/>), set by the token filter of the portal before the action runs; <c>null</c>
/// outside a request of the portal. Scoped to the request.
/// </summary>
public interface IGuestPortalContextAccessor
{
    PortalContext? Current { get; }
}
