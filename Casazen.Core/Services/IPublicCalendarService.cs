namespace Casazen.Core.Services;

/// <summary>
/// The calendar of a property on the public booking site (DB-03b): for each day, whether a stay can start that night, what that
/// night costs and the shortest stay that can start on it. The price of a night is the one the quote adds up
/// (<see cref="StayPricing.NightPrice"/>), the taken nights are the ones the booking checks refuse (<see cref="PropertyOccupancy"/>,
/// <see cref="CheckoutHolds.OccupiesDates"/>): the calendar has no rule of its own. It does not tell a booking from a block of the host
/// from an iCal block, and it never says who stays.
/// </summary>
public interface IPublicCalendarService
{
    /// <summary>
    /// The days of <paramref name="propertyId"/> from <paramref name="from"/> (included) to <paramref name="to"/> (excluded). Without
    /// them: from today (Europe/Rome) for <see cref="PublicCalendar.DefaultMonths"/> months. Days before today are not listed.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">
    /// <see cref="PublicAvailabilityErrorCodes.PropertyNotFound"/>: the property does not exist or is not published
    /// (<see cref="PublicListing.IsPublished"/>, active org).
    /// </exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="PublicCalendarErrorCodes.InvalidRange"/>: <paramref name="to"/> is not after <paramref name="from"/>, or the range is
    /// longer than <see cref="PublicCalendar.MaxMonths"/> months.
    /// </exception>
    Task<PublicCalendar> GetAsync(Guid propertyId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same for a property of a site, by its slug or its id (the org is the site's: a property of another org is not found).
    /// </summary>
    Task<PublicCalendar> GetAsync(
        Guid orgId,
        string propertySlugOrId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken = default);
}

/// <summary>The days of a property from <paramref name="From"/> (included) to <paramref name="To"/> (excluded), as the guests see them.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="From">First day listed: the requested one, or today when that is earlier (days in the past are not listed).</param>
/// <param name="To">The end of the range, excluded, as requested.</param>
/// <param name="Today">Today in Europe/Rome: the days before it are over.</param>
/// <param name="Currency">ISO 4217, always EUR.</param>
/// <param name="Days">One entry for each day from <paramref name="From"/> to <paramref name="To"/>, in order.</param>
/// <param name="GeneratedAt">When the answer was worked out (UTC).</param>
public sealed record PublicCalendar(
    Guid PropertyId,
    DateOnly From,
    DateOnly To,
    DateOnly Today,
    string Currency,
    IReadOnlyList<PublicCalendarDay> Days,
    DateTime GeneratedAt)
{
    /// <summary>Longest range one request may ask for: 13 months, a whole year of bookings plus the month being booked.</summary>
    public const int MaxMonths = 13;

    /// <summary>Months asked when the request names none.</summary>
    public const int DefaultMonths = 12;
}

/// <summary>One night of the calendar, the night that starts on <paramref name="Date"/>.</summary>
/// <param name="Date">The calendar date (Europe/Rome) the night starts on: the check-in day of a stay that starts on it.</param>
/// <param name="Available">
/// A stay can start that night: no booking, hold, "pay at the property" request, iCal block or manual block takes it. The check-out
/// day of a stay is free for a new check-in, so it is available.
/// </param>
/// <param name="NightlyPriceCents">
/// What the night costs, in cents: the nightly rate, with the weekend surcharge on a Friday or a Saturday, or the price the host confirmed
/// for that date (<see cref="StayPricing"/>). Given for a taken night too: it is the price the night would have.
/// </param>
/// <param name="MinNights">The fewest nights of a stay that starts on this day (1 when the property has no minimum).</param>
public sealed record PublicCalendarDay(DateOnly Date, bool Available, long NightlyPriceCents, int MinNights);

/// <summary>Stable <c>code</c> values of the public calendar errors: never rename one.</summary>
public static class PublicCalendarErrorCodes
{
    /// <summary>422: the end of the range is not after its start, or the range is longer than 13 months.</summary>
    public const string InvalidRange = "public_calendar_range_invalid";
}
