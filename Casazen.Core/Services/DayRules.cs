using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// The price and the rules of one night of a property as the services read them (E08/F05/US01/T02, NF-36): what a <see cref="PropertyDayRule"/> row
/// holds, or <see cref="None"/> for a night without a row. A value left unset means «no rule of its own»: the nightly rate, the weekend surcharge
/// or a seasonal price decide the price, the minimum stay of the property decides the minimum.
/// </summary>
/// <param name="Price">The price of the night, two decimals between <see cref="DayRuleLimits.MinPrice"/> and <see cref="DayRuleLimits.MaxPrice"/>.</param>
/// <param name="MinNights">The fewest nights of a stay that starts on the night.</param>
/// <param name="MaxNights">The most nights of a stay that starts on the night.</param>
/// <param name="ClosedToArrival">A stay cannot start on the night.</param>
/// <param name="ClosedToDeparture">A stay cannot end on the day (the day of the departure).</param>
/// <param name="MinAdvanceDays">The fewest days ahead of the night a stay may be booked.</param>
/// <param name="MaxAdvanceDays">The most days ahead of the night a stay may be booked.</param>
public sealed record DayRuleValues(
    decimal? Price = null,
    int? MinNights = null,
    int? MaxNights = null,
    bool ClosedToArrival = false,
    bool ClosedToDeparture = false,
    int? MinAdvanceDays = null,
    int? MaxAdvanceDays = null)
{
    public static DayRuleValues None { get; } = new();

    public bool HasAny =>
        Price is not null
        || MinNights is not null
        || MaxNights is not null
        || ClosedToArrival
        || ClosedToDeparture
        || MinAdvanceDays is not null
        || MaxAdvanceDays is not null;

    public static DayRuleValues Of(PropertyDayRule row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new DayRuleValues(
            row.Price,
            row.MinNights,
            row.MaxNights,
            row.ClosedToArrival,
            row.ClosedToDeparture,
            row.MinAdvanceDays,
            row.MaxAdvanceDays);
    }

    public void WriteTo(PropertyDayRule row)
    {
        ArgumentNullException.ThrowIfNull(row);

        row.Price = Price;
        row.MinNights = MinNights;
        row.MaxNights = MaxNights;
        row.ClosedToArrival = ClosedToArrival;
        row.ClosedToDeparture = ClosedToDeparture;
        row.MinAdvanceDays = MinAdvanceDays;
        row.MaxAdvanceDays = MaxAdvanceDays;
    }
}

public static class DayRuleLimits
{
    public const decimal MinPrice = PropertyDayRule.MinPrice;

    public const decimal MaxPrice = PropertyDayRule.MaxPrice;

    public const int MinStayNights = PropertyDayRule.MinStayNights;

    public const int MaxStayNights = PropertyDayRule.MaxStayNights;

    public const int MinAdvance = PropertyDayRule.MinAdvance;

    public const int MaxAdvance = PropertyDayRule.MaxAdvance;

    public const int MaxRangeDays = 366;
}

public enum FieldPatchKind
{
    Keep = 0,

    Set = 1,

    Clear = 2,
}

public readonly record struct FieldPatch<T> where T : struct
{
    private FieldPatch(FieldPatchKind kind, T value)
    {
        Kind = kind;
        Value = value;
    }

    public FieldPatchKind Kind { get; }

    public T Value { get; }

    public static FieldPatch<T> Keep => default;

    public static FieldPatch<T> Clear => new(FieldPatchKind.Clear, default);

    public static FieldPatch<T> Set(T value) => new(FieldPatchKind.Set, value);

    public T? ApplyTo(T? current) => Kind switch
    {
        FieldPatchKind.Set => Value,
        FieldPatchKind.Clear => null,
        _ => current,
    };
}

public sealed record DayRulePatch(
    FieldPatch<decimal> Price = default,
    FieldPatch<int> MinNights = default,
    FieldPatch<int> MaxNights = default,
    FieldPatch<bool> ClosedToArrival = default,
    FieldPatch<bool> ClosedToDeparture = default,
    FieldPatch<int> MinAdvanceDays = default,
    FieldPatch<int> MaxAdvanceDays = default)
{
    public bool IsEmpty =>
        Price.Kind == FieldPatchKind.Keep
        && MinNights.Kind == FieldPatchKind.Keep
        && MaxNights.Kind == FieldPatchKind.Keep
        && ClosedToArrival.Kind == FieldPatchKind.Keep
        && ClosedToDeparture.Kind == FieldPatchKind.Keep
        && MinAdvanceDays.Kind == FieldPatchKind.Keep
        && MaxAdvanceDays.Kind == FieldPatchKind.Keep;

    public DayRuleValues ApplyTo(DayRuleValues current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var price = Price.ApplyTo(current.Price);
        return new DayRuleValues(
            price is { } set ? DayRuleRules.RoundPrice(set) : null,
            MinNights.ApplyTo(current.MinNights),
            MaxNights.ApplyTo(current.MaxNights),
            ClosedToArrival.ApplyTo(current.ClosedToArrival) ?? false,
            ClosedToDeparture.ApplyTo(current.ClosedToDeparture) ?? false,
            MinAdvanceDays.ApplyTo(current.MinAdvanceDays),
            MaxAdvanceDays.ApplyTo(current.MaxAdvanceDays));
    }
}

public static class DayRuleErrorCodes
{
    public const string InvalidPrice = "calendar_day_rule_invalid_price";

    public const string InvalidStay = "calendar_day_rule_invalid_stay";

    public const string InvalidAdvance = "calendar_day_rule_invalid_advance";

    public const string RangeInvalid = "calendar_day_rule_range_invalid";

    public const string RangeTooLong = "calendar_day_rule_range_too_long";

    public const string InPast = "calendar_day_rule_in_past";

    public const string NothingToChange = "calendar_day_rule_nothing_to_change";

    public const string InvalidPriceMessageKey = "CalendarDayRuleInvalidPrice";
    public const string InvalidStayMessageKey = "CalendarDayRuleInvalidStay";
    public const string InvalidAdvanceMessageKey = "CalendarDayRuleInvalidAdvance";
    public const string RangeInvalidMessageKey = "CalendarDayRuleRangeInvalid";
    public const string RangeTooLongMessageKey = "CalendarDayRuleRangeTooLong";
    public const string InPastMessageKey = "CalendarDayRuleInPast";
    public const string NothingToChangeMessageKey = "CalendarDayRuleNothingToChange";

    public static string MessageKeyOf(string code) => code switch
    {
        InvalidPrice => InvalidPriceMessageKey,
        InvalidStay => InvalidStayMessageKey,
        InvalidAdvance => InvalidAdvanceMessageKey,
        RangeInvalid => RangeInvalidMessageKey,
        RangeTooLong => RangeTooLongMessageKey,
        InPast => InPastMessageKey,
        NothingToChange => NothingToChangeMessageKey,
        _ => code,
    };
}

public static class DayRuleRules
{
    public static decimal RoundPrice(decimal price) => Math.Round(price, 2, MidpointRounding.AwayFromZero);

    public static string? Validate(DayRuleValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Price is { } price && RoundPrice(price) is var cents && (cents < DayRuleLimits.MinPrice || cents > DayRuleLimits.MaxPrice))
            return DayRuleErrorCodes.InvalidPrice;

        if (Outside(values.MinNights, DayRuleLimits.MinStayNights, DayRuleLimits.MaxStayNights)
            || Outside(values.MaxNights, DayRuleLimits.MinStayNights, DayRuleLimits.MaxStayNights)
            || MaximumBelowMinimum(values.MinNights, values.MaxNights))
        {
            return DayRuleErrorCodes.InvalidStay;
        }

        if (Outside(values.MinAdvanceDays, DayRuleLimits.MinAdvance, DayRuleLimits.MaxAdvance)
            || Outside(values.MaxAdvanceDays, DayRuleLimits.MinAdvance, DayRuleLimits.MaxAdvance)
            || MaximumBelowMinimum(values.MinAdvanceDays, values.MaxAdvanceDays))
        {
            return DayRuleErrorCodes.InvalidAdvance;
        }

        return null;
    }

    public static void EnsureValid(DayRuleValues values)
    {
        if (Validate(values) is { } code)
            throw ProblemOf(code);
    }

    public static void EnsureRange(DateOnly from, DateOnly toInclusive)
    {
        if (toInclusive < from)
            throw ProblemOf(DayRuleErrorCodes.RangeInvalid);

        if (toInclusive.DayNumber - from.DayNumber + 1 > DayRuleLimits.MaxRangeDays)
            throw ProblemOf(DayRuleErrorCodes.RangeTooLong);
    }

    public static DomainRuleException ProblemOf(string code) => code switch
    {
        DayRuleErrorCodes.InvalidPrice => new DomainRuleException(
            code, DayRuleErrorCodes.MessageKeyOf(code), DayRuleLimits.MinPrice, DayRuleLimits.MaxPrice),
        DayRuleErrorCodes.InvalidStay => new DomainRuleException(
            code, DayRuleErrorCodes.MessageKeyOf(code), DayRuleLimits.MinStayNights, DayRuleLimits.MaxStayNights),
        DayRuleErrorCodes.InvalidAdvance => new DomainRuleException(
            code, DayRuleErrorCodes.MessageKeyOf(code), DayRuleLimits.MinAdvance, DayRuleLimits.MaxAdvance),
        DayRuleErrorCodes.RangeTooLong => new DomainRuleException(code, DayRuleErrorCodes.MessageKeyOf(code), DayRuleLimits.MaxRangeDays),
        _ => new DomainRuleException(code, DayRuleErrorCodes.MessageKeyOf(code)),
    };

    /// <summary>
    /// Today on the calendar of a property: the date it is now in <paramref name="timezoneId"/> (an IANA id), through
    /// <see cref="TimezoneHelper.ConvertUtcToLocal"/>. The clock is the caller's (<paramref name="utcNow"/>, from an injected
    /// <see cref="TimeProvider"/>). A zone that is missing or that the system does not know falls back to Europe/Rome, the calendar of the
    /// market. Only «today» is zoned: the stay dates are calendar dates of the property and are never converted.
    /// </summary>
    public static DateOnly TodayOf(string? timezoneId, DateTimeOffset utcNow)
    {
        var zone = string.IsNullOrWhiteSpace(timezoneId) ? RomeCalendar.TimeZoneId : timezoneId.Trim();
        try
        {
            return DateOnly.FromDateTime(TimezoneHelper.ConvertUtcToLocal(utcNow.UtcDateTime, zone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return DateOnly.FromDateTime(TimezoneHelper.ConvertUtcToLocal(utcNow.UtcDateTime, RomeCalendar.TimeZoneId));
        }
    }

    private static bool Outside(int? value, int min, int max) => value is { } number && (number < min || number > max);

    private static bool MaximumBelowMinimum(int? minimum, int? maximum) => minimum is { } low && maximum is { } high && high < low;
}

public enum DayPriceSource
{
    DayRule,

    Seasonal,

    Weekend,

    Base,
}

public static class DayRulePricing
{
    public static IReadOnlyDictionary<DateOnly, decimal> HostPrices(
        IReadOnlyDictionary<DateOnly, decimal>? seasonal,
        IReadOnlyDictionary<DateOnly, DayRuleValues>? rules)
    {
        var prices = seasonal is null ? new Dictionary<DateOnly, decimal>() : new Dictionary<DateOnly, decimal>(seasonal);
        if (rules is null)
            return prices;

        foreach (var (night, rule) in rules)
        {
            if (rule.Price is { } price)
                prices[night] = price;
        }

        return prices;
    }

    public static DayPriceSource SourceOf(
        DateOnly night,
        DayRuleValues? rule,
        IReadOnlyDictionary<DateOnly, decimal>? seasonal,
        decimal weekendSurchargePercent)
    {
        if (rule?.Price is not null)
            return DayPriceSource.DayRule;

        if (seasonal is not null && seasonal.ContainsKey(night))
            return DayPriceSource.Seasonal;

        return PropertyStayRules.NormalizeWeekendSurcharge(weekendSurchargePercent) > 0m && PropertyStayRules.IsWeekendNight(night)
            ? DayPriceSource.Weekend
            : DayPriceSource.Base;
    }
}

public static class DayRuleDefaults
{
    public static int MinNightsOf(DayRuleValues? rule, int? propertyMinNights) => rule?.MinNights ?? propertyMinNights ?? 1;
}
