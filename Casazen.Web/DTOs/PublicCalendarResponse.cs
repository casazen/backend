using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The calendar of a property on the public booking site (DB-03b): for each day, whether a stay can start that night, what that night
/// costs and the shortest stay that can start on it. Dates only and amounts in cents: never who stays, and no difference between a
/// booking, a block of the host and an iCal block.
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

    public string Currency { get; init; } = "EUR";

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
    /// confirmed for that date. The same number as the line of the quote of a one-night stay on that date. Given for a taken night too.
    /// </summary>
    public long NightlyPriceCents { get; init; }

    /// <summary>The fewest nights of a stay that starts on this day: 1 when the property has no minimum.</summary>
    public int MinNights { get; init; }

    public static PublicCalendarDayResponse From(PublicCalendarDay day) => new()
    {
        Date = day.Date,
        Available = day.Available,
        NightlyPriceCents = day.NightlyPriceCents,
        MinNights = day.MinNights,
    };
}
