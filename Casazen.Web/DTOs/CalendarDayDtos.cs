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
    public DateOnly Today { get; init; }

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

public sealed class CalendarDaysPropertyDto
{
    public Guid PropertyId { get; init; }

    public string Timezone { get; init; } = string.Empty;

    public string Currency { get; init; } = string.Empty;

    public bool Mirrored { get; init; }

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

public sealed class CalendarDayDto
{
    public DateOnly Date { get; init; }

    public long PriceCents { get; init; }

    public DayPriceSource PriceSource { get; init; }

    public int MinNights { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxNights { get; init; }

    public bool ClosedToArrival { get; init; }

    public bool ClosedToDeparture { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinAdvanceDays { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxAdvanceDays { get; init; }

    public bool HasRule { get; init; }

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
