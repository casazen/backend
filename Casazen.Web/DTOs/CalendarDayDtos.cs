using System.Text.Json.Serialization;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The price and the rules of every night of the properties a caller reaches (E08/F05/US01/T02, NF-36, NF-78, <c>GET /api/calendar/days</c>): definitions
/// in <see cref="IPropertyDayRuleService"/> and <c>docs/TECHNICAL.md</c> ("Host calendar"). Days are calendar dates of the property (<c>yyyy-MM-dd</c>,
/// never converted), money is in cents of the currency of the property, enums are names. A value that is not set is absent, never <c>null</c>.
/// </summary>
public sealed class CalendarDaysDto
{
    /// <summary>Today in Europe/Rome; each property says its own time zone.</summary>
    public DateOnly Today { get; init; }

    /// <summary>The properties the caller reaches (at most 50), by name.</summary>
    public IReadOnlyList<CalendarDaysPropertyDto> Properties { get; init; } = [];

    public static CalendarDaysDto Of(CalendarDays days)
    {
        ArgumentNullException.ThrowIfNull(days);

        return new CalendarDaysDto
        {
            Today = days.Today,
            Properties = days.Properties.Select(CalendarDaysPropertyDto.From).ToList(),
        };
    }
}

/// <summary>A property of the calendar with its nights.</summary>
public sealed class CalendarDaysPropertyDto
{
    public Guid PropertyId { get; init; }

    /// <summary>The IANA time zone of the property: its «today» is read on this clock; a stay date is never converted.</summary>
    public string Timezone { get; init; } = string.Empty;

    /// <summary>ISO 4217 code of the property (EUR or CHF): <see cref="CalendarDayDto.PriceCents"/> is in cents of it.</summary>
    public string Currency { get; init; } = string.Empty;

    /// <summary>The property books into the channel manager: what the host changes here can be sent there.</summary>
    public bool Mirrored { get; init; }

    /// <summary>One entry for each night from the first to the last day asked, in order.</summary>
    public IReadOnlyList<CalendarDayDto> Days { get; init; } = [];

    public static CalendarDaysPropertyDto From(CalendarDaysProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return new CalendarDaysPropertyDto
        {
            PropertyId = property.PropertyId,
            Timezone = property.Timezone,
            Currency = property.Currency,
            Mirrored = property.Mirrored,
            Days = property.Days.Select(CalendarDayDto.From).ToList(),
        };
    }
}

/// <summary>The night that starts on <see cref="Date"/>.</summary>
public sealed class CalendarDayDto
{
    public DateOnly Date { get; init; }

    /// <summary>What the night costs, in cents: the number of the quote of a one-night stay on this date.</summary>
    public long PriceCents { get; init; }

    /// <summary><c>DayRule</c> (the host set a price here), <c>Seasonal</c> (a seasonal price it confirmed), <c>Weekend</c> or <c>Base</c> (the nightly rate).</summary>
    public DayPriceSource PriceSource { get; init; }

    /// <summary>The fewest nights of a stay that starts on this night: the rule, else the minimum of the property, else 1.</summary>
    public int MinNights { get; init; }

    /// <summary>The most nights of a stay that starts on this night; absent for no maximum.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxNights { get; init; }

    /// <summary>A stay cannot start on this night.</summary>
    public bool ClosedToArrival { get; init; }

    /// <summary>A stay cannot end on this day.</summary>
    public bool ClosedToDeparture { get; init; }

    /// <summary>The fewest days ahead a stay may be booked; absent for no minimum.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinAdvanceDays { get; init; }

    /// <summary>The most days ahead a stay may be booked; absent for no maximum.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxAdvanceDays { get; init; }

    /// <summary>The night has a rule of its own.</summary>
    public bool HasRule { get; init; }

    /// <summary>A booking, a hold, a request or a block takes the night.</summary>
    public bool Taken { get; init; }

    public static CalendarDayDto From(CalendarDay day)
    {
        ArgumentNullException.ThrowIfNull(day);

        return new CalendarDayDto
        {
            Date = day.Date,
            PriceCents = day.PriceCents,
            PriceSource = day.PriceSource,
            MinNights = day.MinNights,
            MaxNights = day.MaxNights,
            ClosedToArrival = day.ClosedToArrival,
            ClosedToDeparture = day.ClosedToDeparture,
            MinAdvanceDays = day.MinAdvanceDays,
            MaxAdvanceDays = day.MaxAdvanceDays,
            HasRule = day.HasRule,
            Taken = day.Taken,
        };
    }
}
