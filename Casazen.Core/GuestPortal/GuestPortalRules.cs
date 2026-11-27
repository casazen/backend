using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.GuestPortal;

/// <summary>
/// The rules of the stay link of the guest portal (E50/F01/US01/T01, NF-152, decision A2): which bookings get one, how long it opens, and the stable
/// codes the host API answers with. The single place that says it: the link service, the host endpoints and the portal read it from here.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Eligible</b>: a booking whose status is one of the active ones of the code (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>:
/// confirmed, checked in, checked out), of <b>any source</b>, and not marked as a no-show (NF-45: the guests never came). A request, a hold and a
/// cancelled booking get no new link. The one definition of the active statuses is reused, never a copied list.</item>
/// <item><b>Validity</b>: a link opens until the end of the 30th day after the departure, <see cref="ExpiryFor"/>; a stay whose link would already
/// be over gets none (<see cref="StayEndedReason"/>).</item>
/// <item><b>Resolution</b>: an existing link keeps opening for a booking that was cancelled or marked as a no-show afterwards (the portal shows the
/// stay as it is, and a late arrival clears the no-show): <see cref="ResolvableStatuses"/>. It never opens for a booking that is only a request.</item>
/// </list>
/// </remarks>
public static class GuestPortalRules
{
    /// <summary>Days after the departure the link keeps opening: the link works up to the end of the 30th one.</summary>
    public const int DaysAfterDeparture = 30;

    /// <summary>409 of the host endpoints: the booking cannot get a stay link (<see cref="IneligibleReason"/>).</summary>
    public const string BookingNotEligibleCode = "guest_portal_booking_not_eligible";

    /// <summary>The one 404 of the public portal: a token that is unknown, revoked, expired or not a token at all.</summary>
    public const string LinkInvalidCode = "guest_portal_link_invalid";

    /// <summary>Why a booking gets no link: its status (a request, cancelled, a no-show).</summary>
    public const string BookingStatusReason = "booking_status";

    /// <summary>Why a booking gets no link: the stay ended more than <see cref="DaysAfterDeparture"/> days ago.</summary>
    public const string StayEndedReason = "stay_ended";

    /// <summary>State of the link of a booking for the host: it has none (or only revoked ones).</summary>
    public const string StateNone = "none";

    /// <summary>State of the link of a booking for the host: the active link opens.</summary>
    public const string StateActive = "active";

    /// <summary>State of the link of a booking for the host: the active link no longer opens (its stay ended).</summary>
    public const string StateExpired = "expired";

    /// <summary>
    /// The statuses a link resolves for: the active ones and the cancelled one (the guest still reads the stay); never a request. An array, like
    /// <see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, so the queries translate it.
    /// </summary>
    public static readonly BookingStatus[] ResolvableStatuses = [.. AlloggiatiStatusRules.ActiveBookingStatuses, BookingStatus.Cancelled];

    /// <summary>The shortest token that can be one: 128 random bits in base64url (22 characters). Anything shorter is not even looked up.</summary>
    public const int MinTokenLength = 22;

    /// <summary>
    /// True when <paramref name="token"/> can be a token of a stay link: base64url characters only, at least <see cref="MinTokenLength"/> and at most
    /// <see cref="GuestPortalLink.TokenMaxLength"/> long. A token that is not is answered like an unknown one, without a query.
    /// </summary>
    public static bool IsWellFormedToken(string? token)
    {
        if (token is null || token.Length < MinTokenLength || token.Length > GuestPortalLink.TokenMaxLength)
            return false;

        foreach (var c in token)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }

        return true;
    }

    /// <summary>True when <paramref name="status"/> is one of the active statuses of the code (confirmed, checked in, checked out).</summary>
    public static bool IsEligibleStatus(BookingStatus status) => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(status);

    /// <summary>
    /// True when the stay of <paramref name="status"/> and <paramref name="noShowAt"/> may be shown the reserved data of the house (the precise address,
    /// the access instructions, the door code, the Wi-Fi) once its check-in is completed: a stay still to come or in progress, that is a confirmed or a
    /// checked-in booking that is not a no-show. The link keeps opening for a cancelled stay, a no-show and a stay already checked out (the guest still
    /// reads what it was), but they get no reserved data, whatever their check-in says: it is the rule of the lock code of the guest
    /// (<c>IAccessCodeService.ReleaseToGuestAsync</c>: the one definition of an active stay, <see cref="AlloggiatiStatusRules.IsDue"/>, and not one
    /// already over). Read only by <c>PortalContextFactory</c>, the one place that sets <see cref="PortalContext.CanSeeReserved"/>.
    /// </summary>
    public static bool IsStayOpenToReservedData(BookingStatus status, DateTime? noShowAt) =>
        AlloggiatiStatusRules.ActiveBookingStatuses.Contains(status) && status != BookingStatus.CheckedOut && noShowAt is null;

    /// <summary>
    /// The end of the 30th day after the departure, in UTC: <c>checkOutDate.Date + 31 days</c>. A departure on 13 October gives a link that opens
    /// until 13 November 00:00 UTC, the end of 12 November.
    /// </summary>
    public static DateTime ExpiryFor(DateTime checkOutDate) =>
        DateTime.SpecifyKind(checkOutDate.Date.AddDays(DaysAfterDeparture + 1), DateTimeKind.Utc);

    /// <summary>
    /// When a link stored with <paramref name="storedExpiresAt"/> stops opening for a stay that leaves on <paramref name="checkOutDate"/>: the later of
    /// the two, so a departure moved later extends the link and one moved earlier never shortens it.
    /// </summary>
    public static DateTime EffectiveExpiry(DateTime storedExpiresAt, DateTime checkOutDate)
    {
        var expiry = ExpiryFor(checkOutDate);
        return storedExpiresAt >= expiry ? storedExpiresAt : expiry;
    }

    /// <summary>
    /// Why <paramref name="booking"/> cannot get a stay link at <paramref name="nowUtc"/>: <see cref="BookingStatusReason"/> (not an active status, or a
    /// no-show), <see cref="StayEndedReason"/> (the link would already be over), or <c>null</c> when it can.
    /// </summary>
    public static string? IneligibleReason(Booking booking, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return IneligibleReason(booking.Status, booking.NoShowAt, booking.CheckOutDate, nowUtc);
    }

    /// <inheritdoc cref="IneligibleReason(Booking, DateTime)"/>
    public static string? IneligibleReason(BookingStatus status, DateTime? noShowAt, DateTime checkOutDate, DateTime nowUtc)
    {
        if (!IsEligibleStatus(status) || noShowAt is not null)
            return BookingStatusReason;

        return ExpiryFor(checkOutDate) <= nowUtc ? StayEndedReason : null;
    }
}
