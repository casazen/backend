using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// Which bookings can be checked in and out, and when (CO-08, A5-08). One rule set for the arrival registration, the
/// check-out wizard and <c>POST /api/bookings/{id}/check-out</c>. Days are calendar dates in Europe/Rome
/// (<c>RomeCalendar.TodayInRome</c>) compared with the date-only stay dates.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Arrival: a <see cref="BookingStatus.Confirmed"/> booking, from its check-in day to its check-out day included.
/// Registering it on a later day of the stay is normal (the host forgot): never "check-in in the past". After the
/// check-out day the stay is closed with the check-out, which registers the arrival too.</item>
/// <item>Check-out: a <see cref="BookingStatus.CheckedIn"/> stay from its check-in day (an early departure is allowed:
/// the dates stay taken and the price does not change), or a confirmed booking whose arrival the host registers in the
/// same operation. Without that confirmation a confirmed booking answers 409
/// <see cref="BookingErrorCodes.ArrivalNotRegistered"/>, so the client can offer "registra arrivo e procedi".</item>
/// </list>
/// </remarks>
public static class StayLifecycleRules
{
    /// <summary>The error that stops the arrival registration of a booking, or null when it can be registered.</summary>
    public static DomainException? ArrivalError(Booking booking, DateTime todayInRome)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return booking.Status switch
        {
            BookingStatus.CheckedIn => new DomainConflictException(BookingErrorCodes.AlreadyCheckedIn, "BookingAlreadyCheckedIn"),
            BookingStatus.Confirmed when todayInRome.Date < booking.CheckInDate.Date => ArrivalTooEarly(booking),
            BookingStatus.Confirmed when todayInRome.Date > booking.CheckOutDate.Date =>
                new DomainRuleException(BookingErrorCodes.ArrivalAfterDeparture, "BookingArrivalAfterDeparture"),
            BookingStatus.Confirmed => null,
            _ => new DomainConflictException(BookingErrorCodes.NotConfirmed, "BookingArrivalNotConfirmed"),
        };
    }

    /// <summary>
    /// The error that stops the check-out of a booking (start or completion), or null when it can be checked out.
    /// </summary>
    /// <param name="booking">The booking, read under its lock.</param>
    /// <param name="todayInRome">Today in Europe/Rome (midnight UTC of the date).</param>
    /// <param name="registerArrival">The host confirms that the guest arrived (see <see cref="StayCheckOut"/>).</param>
    public static DomainException? CheckOutError(Booking booking, DateTime todayInRome, bool registerArrival)
    {
        ArgumentNullException.ThrowIfNull(booking);

        if (booking.Status == BookingStatus.CheckedOut)
            return new DomainConflictException(BookingErrorCodes.AlreadyCheckedOut, "BookingAlreadyCheckedOut");
        if (booking.Status is not (BookingStatus.CheckedIn or BookingStatus.Confirmed))
            return new DomainConflictException(BookingErrorCodes.NotCheckedIn, "BookingNotCheckedIn");
        if (todayInRome.Date < booking.CheckInDate.Date)
            return new DomainRuleException(BookingErrorCodes.CheckOutTooEarly, "BookingCheckOutTooEarly");
        if (booking.Status == BookingStatus.Confirmed && !registerArrival)
            return new DomainConflictException(BookingErrorCodes.ArrivalNotRegistered, "BookingArrivalNotRegistered");
        return null;
    }

    /// <summary>
    /// Check-outs due, for the compliance cockpit (<c>checkoutsDue</c>): the departures of today (confirmed or checked
    /// in), and the checked-in stays whose departure day has passed without a check-out. Confirmed bookings of past days
    /// are not listed: until CO-08 no arrival could be registered from the apps, so every past stay is still confirmed.
    /// Translatable by EF (date-only values compared with the bounds of the day).
    /// </summary>
    public static Expression<Func<Booking, bool>> CheckOutDue(DateTime todayInRome)
    {
        var today = todayInRome.Date;
        var tomorrow = today.AddDays(1);
        return b => (b.Status == BookingStatus.CheckedIn && b.CheckOutDate < tomorrow)
            || (b.Status == BookingStatus.Confirmed && b.CheckOutDate >= today && b.CheckOutDate < tomorrow);
    }

    private static DomainRuleException ArrivalTooEarly(Booking booking) =>
        new(BookingErrorCodes.ArrivalTooEarly, "BookingArrivalTooEarly", booking.CheckInDate.ToString("dd/MM/yyyy"));
}
