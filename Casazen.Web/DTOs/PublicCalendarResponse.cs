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

    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    public DateOnly Today { get; init; }

    public string Currency { get; init; } = StayCurrency.Default;

    public List<PublicCalendarDayResponse> Days { get; init; } = [];

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

public record PublicCalendarDayResponse
{
    public DateOnly Date { get; init; }

    public bool Available { get; init; }

    public long NightlyPriceCents { get; init; }

    public int MinNights { get; init; }

    public int? MaxNights { get; init; }

    public bool ClosedToArrival { get; init; }

    public bool ClosedToDeparture { get; init; }

    public int? MinAdvanceDays { get; init; }

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
