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
    public const int DaysAfterDeparture = 30;

    public const string BookingNotEligibleCode = "guest_portal_booking_not_eligible";

    public const string LinkInvalidCode = "guest_portal_link_invalid";

    public const string BookingStatusReason = "booking_status";

    public const string StayEndedReason = "stay_ended";

    public const string StateNone = "none";

    public const string StateActive = "active";

    public const string StateExpired = "expired";

    public static readonly BookingStatus[] ResolvableStatuses = [.. AlloggiatiStatusRules.ActiveBookingStatuses, BookingStatus.Cancelled];

    public const int MinTokenLength = 22;

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

    public static DateTime ExpiryFor(DateTime checkOutDate) =>
        DateTime.SpecifyKind(checkOutDate.Date.AddDays(DaysAfterDeparture + 1), DateTimeKind.Utc);

    public static DateTime EffectiveExpiry(DateTime storedExpiresAt, DateTime checkOutDate)
    {
        var expiry = ExpiryFor(checkOutDate);
        return storedExpiresAt >= expiry ? storedExpiresAt : expiry;
    }

    public static string? IneligibleReason(Booking booking, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return IneligibleReason(booking.Status, booking.NoShowAt, booking.CheckOutDate, nowUtc);
    }

    public static string? IneligibleReason(BookingStatus status, DateTime? noShowAt, DateTime checkOutDate, DateTime nowUtc)
    {
        if (!IsEligibleStatus(status) || noShowAt is not null)
            return BookingStatusReason;

        return ExpiryFor(checkOutDate) <= nowUtc ? StayEndedReason : null;
    }
}
