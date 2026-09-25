using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Regulatory;

/// <summary>
/// Timing rules of the Alloggiati Web communication (CO-11, A5-03, A5-37). Sources:
/// <c>.claude/context/regulations/alloggiati.md</c>, sections "Vincoli applicativi del portale" and
/// "5. Tempistiche di legge" (verified by RS-1).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Art. 109, comma 3, TULPS: within 24 hours of arrival, and within 6 hours for stays not longer than
/// 24 hours.</item>
/// <item>The portal accepts as arrival date only today or yesterday: nothing can be sent before the arrival day,
/// which is counted in Europe/Rome.</item>
/// <item>At most 30 days of stay per schedina.</item>
/// </list>
/// Booking check-in/check-out dates are date-only values (midnight UTC of the calendar date) and carry no time of
/// day. Without the times, a stay of at most one night may last 24 hours or less, so it gets the stricter 6-hour
/// term: the deadline shown is never later than the legal one.
/// </remarks>
public static class AlloggiatiTerms
{
    /// <summary>Ordinary term: 24 hours from arrival.</summary>
    public static readonly TimeSpan OrdinaryTerm = TimeSpan.FromHours(24);

    /// <summary>Stays not longer than 24 hours: 6 hours from arrival.</summary>
    public static readonly TimeSpan ShortStayTerm = TimeSpan.FromHours(6);

    /// <summary>Maximum days of stay the portal accepts on one schedina.</summary>
    public const int MaxStayDaysPerSchedina = 30;

    /// <summary>Days of stay (nights) between two date-only values; never negative.</summary>
    public static int StayDays(DateTime checkInDate, DateTime checkOutDate) =>
        Math.Max(0, (checkOutDate.Date - checkInDate.Date).Days);

    /// <summary>True when the stay may last 24 hours or less (same day or one night): the 6-hour term applies.</summary>
    public static bool IsShortStay(DateTime checkInDate, DateTime checkOutDate) =>
        StayDays(checkInDate, checkOutDate) <= 1;

    /// <summary>
    /// Start of the arrival day in Europe/Rome, as a UTC instant: the earliest moment the portal accepts the
    /// arrival date, hence the time the Alloggiati job is scheduled at.
    /// </summary>
    public static DateTime ArrivalDayStartUtc(DateTime checkInDate) => RomeCalendar.StartOfDayUtc(checkInDate);

    /// <summary>
    /// Arrival instant the term runs from: the arrival recorded at check-in (<see cref="Booking.ArrivedAt"/>), or
    /// else the start of the check-in day in Europe/Rome (the earliest possible arrival).
    /// </summary>
    public static DateTime ArrivalInstantUtc(DateTime? arrivedAt, DateTime checkInDate) =>
        arrivedAt ?? ArrivalDayStartUtc(checkInDate);

    /// <summary>Legal deadline of the communication, as a UTC instant.</summary>
    public static DateTime DeadlineUtc(DateTime? arrivedAt, DateTime checkInDate, DateTime checkOutDate) =>
        ArrivalInstantUtc(arrivedAt, checkInDate)
        + (IsShortStay(checkInDate, checkOutDate) ? ShortStayTerm : OrdinaryTerm);

    /// <summary>Legal deadline of the communication for <paramref name="booking"/>, as a UTC instant.</summary>
    public static DateTime DeadlineUtc(Booking booking) =>
        DeadlineUtc(booking.ArrivedAt, booking.CheckInDate, booking.CheckOutDate);

    /// <summary>True once the arrival day has started in Europe/Rome (the portal accepts the arrival date).</summary>
    public static bool IsArrivalDayReached(DateTime checkInDate, DateTime todayInRome) =>
        todayInRome.Date >= checkInDate.Date;
}
