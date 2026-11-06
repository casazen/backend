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
    /// <summary>A night with no rule at all.</summary>
    public static DayRuleValues None { get; } = new();

    /// <summary>At least one value is set: the night has a row. A night whose values were all cleared has none (the row is deleted).</summary>
    public bool HasAny =>
        Price is not null
        || MinNights is not null
        || MaxNights is not null
        || ClosedToArrival
        || ClosedToDeparture
        || MinAdvanceDays is not null
        || MaxAdvanceDays is not null;

    /// <summary>The values of a stored row.</summary>
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

    /// <summary>Writes the values into <paramref name="row"/> (the price as it is: the caller rounds and validates before).</summary>
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

/// <summary>
/// The bounds of the values of a day rule and of a change (E08/F05/US01/T02): the ones the database refuses outside of
/// (<see cref="PropertyDayRule.ValuesCheckName"/>, written from the same numbers) and the longest range a change covers.
/// </summary>
public static class DayRuleLimits
{
    /// <summary>The lowest price of a night: one cent.</summary>
    public const decimal MinPrice = PropertyDayRule.MinPrice;

    /// <summary>The highest price of a night: the range of the nightly rate.</summary>
    public const decimal MaxPrice = PropertyDayRule.MaxPrice;

    /// <summary>The shortest stay a rule asks for, in nights.</summary>
    public const int MinStayNights = PropertyDayRule.MinStayNights;

    /// <summary>The longest stay a rule asks for, in nights.</summary>
    public const int MaxStayNights = PropertyDayRule.MaxStayNights;

    /// <summary>The shortest advance a rule asks for, in days.</summary>
    public const int MinAdvance = PropertyDayRule.MinAdvance;

    /// <summary>The longest advance a rule asks for, in days.</summary>
    public const int MaxAdvance = PropertyDayRule.MaxAdvance;

    /// <summary>The most days one change covers, both ends included: a year and the day of a leap year.</summary>
    public const int MaxRangeDays = 366;
}

/// <summary>What a patch does to one value of a day rule.</summary>
public enum FieldPatchKind
{
    /// <summary>The value stays as it is. The default: a field nobody mentions is not touched.</summary>
    Keep = 0,

    /// <summary>The value becomes the one given.</summary>
    Set = 1,

    /// <summary>The value is taken away (no value of its own for the night; a flag goes back to off).</summary>
    Clear = 2,
}

/// <summary>
/// What to do to one value of a day rule: <see cref="Keep"/> it, <see cref="Set(T)"/> it, or <see cref="Clear"/> it (E08/F05/US01/T02). The default
/// is <see cref="Keep"/>, so a patch that names one field leaves the others alone.
/// </summary>
/// <typeparam name="T">The type of the value: a price, a number of nights or days, a flag.</typeparam>
public readonly record struct FieldPatch<T> where T : struct
{
    private FieldPatch(FieldPatchKind kind, T value)
    {
        Kind = kind;
        Value = value;
    }

    public FieldPatchKind Kind { get; }

    /// <summary>The value to set; meaningful only for <see cref="FieldPatchKind.Set"/>.</summary>
    public T Value { get; }

    /// <summary>Leave the value as it is.</summary>
    public static FieldPatch<T> Keep => default;

    /// <summary>Clear the value.</summary>
    public static FieldPatch<T> Clear => new(FieldPatchKind.Clear, default);

    /// <summary>Set the value.</summary>
    public static FieldPatch<T> Set(T value) => new(FieldPatchKind.Set, value);

    /// <summary>The value after the patch: <paramref name="current"/> for <see cref="FieldPatchKind.Keep"/>, the new one for a set, none for a clear.</summary>
    public T? ApplyTo(T? current) => Kind switch
    {
        FieldPatchKind.Set => Value,
        FieldPatchKind.Clear => null,
        _ => current,
    };
}

/// <summary>
/// A change to the rules of the nights of a range, one <see cref="FieldPatch{T}"/> for each value (E08/F05/US01/T02, NF-36). Pure:
/// <see cref="ApplyTo"/> works out the values a night ends up with, <see cref="DayRuleRules.Validate"/> says whether they are allowed.
/// </summary>
public sealed record DayRulePatch(
    FieldPatch<decimal> Price = default,
    FieldPatch<int> MinNights = default,
    FieldPatch<int> MaxNights = default,
    FieldPatch<bool> ClosedToArrival = default,
    FieldPatch<bool> ClosedToDeparture = default,
    FieldPatch<int> MinAdvanceDays = default,
    FieldPatch<int> MaxAdvanceDays = default)
{
    /// <summary>The patch touches no value at all: there is nothing to change.</summary>
    public bool IsEmpty =>
        Price.Kind == FieldPatchKind.Keep
        && MinNights.Kind == FieldPatchKind.Keep
        && MaxNights.Kind == FieldPatchKind.Keep
        && ClosedToArrival.Kind == FieldPatchKind.Keep
        && ClosedToDeparture.Kind == FieldPatchKind.Keep
        && MinAdvanceDays.Kind == FieldPatchKind.Keep
        && MaxAdvanceDays.Kind == FieldPatchKind.Keep;

    /// <summary>
    /// The values a night ends up with when this patch is applied to <paramref name="current"/>. The price is rounded to the cent, half away from
    /// zero, as <see cref="StayPricing"/> rounds every amount; nothing else is changed or checked here.
    /// </summary>
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

/// <summary>
/// Stable <c>code</c> values of the errors of the rules of the nights (E08/F05/US01/T02): part of the API contract, never rename one. Each has the
/// key of its sentence in <c>SharedResources.resx</c> (Italian) and <c>SharedResources.en.resx</c>.
/// </summary>
public static class DayRuleErrorCodes
{
    /// <summary>422: the price is not between the lowest and the highest price of a night, or has more than two decimals once rounded.</summary>
    public const string InvalidPrice = "calendar_day_rule_invalid_price";

    /// <summary>422: a stay is outside the nights allowed, or its maximum is below its minimum.</summary>
    public const string InvalidStay = "calendar_day_rule_invalid_stay";

    /// <summary>422: an advance is outside the days allowed, or its maximum is below its minimum.</summary>
    public const string InvalidAdvance = "calendar_day_rule_invalid_advance";

    /// <summary>422: the last day of the range is before the first.</summary>
    public const string RangeInvalid = "calendar_day_rule_range_invalid";

    /// <summary>422: the range covers more than <see cref="DayRuleLimits.MaxRangeDays"/> days.</summary>
    public const string RangeTooLong = "calendar_day_rule_range_too_long";

    /// <summary>422: every day of the range is already over, on the clock of the property.</summary>
    public const string InPast = "calendar_day_rule_in_past";

    /// <summary>422: the change touches no value, or no day.</summary>
    public const string NothingToChange = "calendar_day_rule_nothing_to_change";

    public const string InvalidPriceMessageKey = "CalendarDayRuleInvalidPrice";
    public const string InvalidStayMessageKey = "CalendarDayRuleInvalidStay";
    public const string InvalidAdvanceMessageKey = "CalendarDayRuleInvalidAdvance";
    public const string RangeInvalidMessageKey = "CalendarDayRuleRangeInvalid";
    public const string RangeTooLongMessageKey = "CalendarDayRuleRangeTooLong";
    public const string InPastMessageKey = "CalendarDayRuleInPast";
    public const string NothingToChangeMessageKey = "CalendarDayRuleNothingToChange";

    /// <summary>The key of the sentence of <paramref name="code"/>; the code itself for one this class does not know.</summary>
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

/// <summary>
/// The rules of the values of a day rule and of a range of days (E08/F05/US01/T02, NF-36): pure functions, so the service that stores them, the
/// preview of a change and the tests agree. A price is above 0 with two decimals (rounded half away from zero), a maximum stay is not below its
/// minimum, a maximum advance is not below its minimum advance.
/// </summary>
public static class DayRuleRules
{
    /// <summary>The price rounded to the cent, half away from zero, as <see cref="StayPricing"/> rounds an amount.</summary>
    public static decimal RoundPrice(decimal price) => Math.Round(price, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The first problem of <paramref name="values"/> as a code of <see cref="DayRuleErrorCodes"/> (the price first, then the stay, then the
    /// advance), or <c>null</c> when they are allowed. The price is judged once rounded to the cent.
    /// </summary>
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

    /// <summary>Throws the 422 of the first problem of <paramref name="values"/>, if any.</summary>
    /// <exception cref="DomainRuleException">One of the <see cref="DayRuleErrorCodes"/> about a value.</exception>
    public static void EnsureValid(DayRuleValues values)
    {
        if (Validate(values) is { } code)
            throw ProblemOf(code);
    }

    /// <summary>The range of days of a change: the last day is not before the first and the range is at most <see cref="DayRuleLimits.MaxRangeDays"/> days.</summary>
    /// <exception cref="DomainRuleException"><see cref="DayRuleErrorCodes.RangeInvalid"/> or <see cref="DayRuleErrorCodes.RangeTooLong"/>.</exception>
    public static void EnsureRange(DateOnly from, DateOnly toInclusive)
    {
        if (toInclusive < from)
            throw ProblemOf(DayRuleErrorCodes.RangeInvalid);

        if (toInclusive.DayNumber - from.DayNumber + 1 > DayRuleLimits.MaxRangeDays)
            throw ProblemOf(DayRuleErrorCodes.RangeTooLong);
    }

    /// <summary>The 422 of <paramref name="code"/>, with the bounds the sentence names.</summary>
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

/// <summary>Where the price of a night in the calendar comes from (E08/F05/US01/T02): the most specific thing the host said about it.</summary>
public enum DayPriceSource
{
    /// <summary>The host set a price for that night in the calendar (a day rule).</summary>
    DayRule,

    /// <summary>The host confirmed the seasonal price proposed for that night.</summary>
    Seasonal,

    /// <summary>The nightly rate with the weekend surcharge of the property (a Friday or a Saturday night).</summary>
    Weekend,

    /// <summary>The nightly rate of the property.</summary>
    Base,
}

/// <summary>
/// The price of a night with the day rules in it (E08/F05/US01/T02, T03): the single definition of which prices the host set for dates, shared by
/// the quote, the checkout, the public calendar, the search and the calendar of the host, so they all price a night the same way.
/// </summary>
public static class DayRulePricing
{
    /// <summary>
    /// The prices the host set for dates: the seasonal prices it confirmed, overridden for a night by the price of the day rule of that night (the
    /// host said it last and most precisely, on the calendar). A night whose rule has no price keeps the seasonal price, or none. This is what
    /// <see cref="StayPricing"/> takes as <c>hostPrices</c>.
    /// </summary>
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

    /// <summary>
    /// Where the price of <paramref name="night"/> comes from: the day rule when it has a price, else the seasonal price the host confirmed,
    /// else the weekend surcharge on a Friday or a Saturday night (when the property has one), else the nightly rate. The order of
    /// <see cref="StayPricing.Lodging"/>.
    /// </summary>
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

/// <summary>What a night means when its rule leaves a value unset (E08/F05/US01/T02): the one place the calendars and the search read it from.</summary>
public static class DayRuleDefaults
{
    /// <summary>The minimum stay of a stay that starts on a night: the rule of the night, else the minimum of the property, else one night.</summary>
    public static int MinNightsOf(DayRuleValues? rule, int? propertyMinNights) => rule?.MinNights ?? propertyMinNights ?? 1;
}
