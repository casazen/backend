using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The calendar of a property on the public booking site (DB-03b): for each day, whether the night is free, what that night costs, the
/// shortest and the longest stay that can start on it and the rules of the day (arrival and departure closed, how far ahead it can be
/// booked, NF-36). Dates, numbers and flags only, amounts in cents: never who stays, and no difference between a booking, a block of the
/// host and an iCal block.
/// </summary>
public record PublicCalendarResponse
{
    public Guid PropertyId { get; init; }

    /// <summary>First day listed (<c>yyyy-MM-dd</c>, included): the day asked for, or today when that is earlier. Days in the past are not listed.</summary>
    public DateOnly StartDate { get; init; }

    /// <summary>The end of the range (<c>yyyy-MM-dd</c>), excluded, as asked.</summary>
    public DateOnly EndDate { get; init; }

    /// <summary>Today in Europe/Rome: the days before it are over.</summary>
    public DateOnly Today { get; init; }

    /// <summary>ISO 4217 code of the property (EUR or CHF, <see cref="StayCurrency"/>): <see cref="PublicCalendarDayResponse.NightlyPriceCents"/> is in cents of it.</summary>
    public string Currency { get; init; } = StayCurrency.Default;

    /// <summary>One entry for each day from <see cref="StartDate"/> to <see cref="EndDate"/>, in order.</summary>
    public List<PublicCalendarDayResponse> Days { get; init; } = [];

    /// <summary>When the answer was worked out (UTC).</summary>
    public DateTime GeneratedAt { get; init; }

    public static PublicCalendarResponse From(PublicCalendar calendar) => new()
    {
        PropertyId = calendar.PropertyId,
        StartDate = calendar.From,
        EndDate = calendar.To,
        Today = calendar.Today,
        Currency = calendar.Currency,
        Days = calendar.Days.Select(PublicCalendarDayResponse.From).ToList(),
        GeneratedAt = calendar.GeneratedAt,
    };
}

/// <summary>The night that starts on <see cref="Date"/>.</summary>
public record PublicCalendarDayResponse
{
    public DateOnly Date { get; init; }

    /// <summary>A stay can start that night. The check-out day of a stay is free for a new check-in, so it is available.</summary>
    public bool Available { get; init; }

    /// <summary>
    /// What the night costs, in cents: the nightly rate, with the weekend surcharge on a Friday or a Saturday, or the price the host
    /// set for that date (a seasonal price it confirmed, or a price it wrote in its calendar, which comes first). The same number as the
    /// line of the quote of a one-night stay on that date. Given for a taken night too.
    /// </summary>
    public long NightlyPriceCents { get; init; }

    /// <summary>The fewest nights of a stay that starts on this day: the rule of the day, else the minimum of the property, else 1.</summary>
    public int MinNights { get; init; }

    /// <summary>The most nights of a stay that starts on this day; <c>null</c> for no maximum (E08/F05/US01/T03).</summary>
    public int? MaxNights { get; init; }

    /// <summary>
    /// A stay cannot start on this night. <see cref="Available"/> still means «the night is free»: a night closed to arrival is free and cannot
    /// start a stay, which this flag says.
    /// </summary>
    public bool ClosedToArrival { get; init; }

    /// <summary>A stay cannot end on this day (the day of the departure).</summary>
    public bool ClosedToDeparture { get; init; }

    /// <summary>The fewest days ahead of this night a stay may be booked; <c>null</c> for no minimum.</summary>
    public int? MinAdvanceDays { get; init; }

    /// <summary>The most days ahead of this night a stay may be booked; <c>null</c> for no maximum.</summary>
    public int? MaxAdvanceDays { get; init; }

    public static PublicCalendarDayResponse From(PublicCalendarDay day) => new()
    {
        Date = day.Date,
        Available = day.Available,
        NightlyPriceCents = day.NightlyPriceCents,
        MinNights = day.MinNights,
        MaxNights = day.MaxNights,
        ClosedToArrival = day.ClosedToArrival,
        ClosedToDeparture = day.ClosedToDeparture,
        MinAdvanceDays = day.MinAdvanceDays,
        MaxAdvanceDays = day.MaxAdvanceDays,
    };
}
