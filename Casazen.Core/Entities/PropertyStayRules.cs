namespace Casazen.Core.Entities;

/// <summary>
/// Rules of the two stay fields of a <see cref="Property"/> that the public quote and checkout read (DB-03, D20): the minimum
/// stay and the weekend surcharge. One place for the limits, for what a "weekend night" is and for the normalization, used
/// by the host forms, the property service, the price of a stay (<c>StayPricing</c>) and the tests.
/// </summary>
public static class PropertyStayRules
{
    /// <summary>Shortest minimum stay a host can set: one night is the same as no minimum, which is <c>null</c>.</summary>
    public const int MinMinNights = 1;

    /// <summary>
    /// Longest minimum stay: 30 nights is the longest short-term let (a longer stay is a different contract, the long-term
    /// mode of the property), and a minimum above the longest stay a guest can book would make the property unbookable.
    /// </summary>
    public const int MaxMinNights = 30;

    /// <summary>Highest weekend surcharge: +100 % is a night that costs double; more is a typing mistake.</summary>
    public const decimal MaxWeekendSurchargePercent = 100m;

    /// <summary>Decimals kept for the surcharge (the column is <c>numeric(5,2)</c>).</summary>
    public const int WeekendSurchargeScale = 2;

    /// <summary>422 (service level, behind the 400 of the forms): the minimum stay is out of range.</summary>
    public const string MinNightsInvalidCode = "property_min_nights_invalid";

    /// <summary>422 (service level, behind the 400 of the forms): the weekend surcharge is out of range.</summary>
    public const string WeekendSurchargeInvalidCode = "property_weekend_surcharge_invalid";

    /// <summary>True for no minimum (<c>null</c>) or a minimum from <see cref="MinMinNights"/> to <see cref="MaxMinNights"/>.</summary>
    public static bool IsValidMinNights(int? minNights) => minNights is null or (>= MinMinNights and <= MaxMinNights);

    /// <summary>True for a surcharge from 0 to <see cref="MaxWeekendSurchargePercent"/> percent.</summary>
    public static bool IsValidWeekendSurcharge(decimal percent) => percent is >= 0m and <= MaxWeekendSurchargePercent;

    /// <summary>The surcharge with the stored precision (<see cref="WeekendSurchargeScale"/> decimals, half away from zero).</summary>
    public static decimal NormalizeWeekendSurcharge(decimal percent) =>
        Math.Round(percent, WeekendSurchargeScale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A night is a weekend night when the evening it starts is a Friday or a Saturday: the nights Friday to Saturday and
    /// Saturday to Sunday, the ones a guest who comes for "the weekend" sleeps. <paramref name="night"/> is the Europe/Rome
    /// calendar date of that evening: the stays of CasaZen are dates without a time (midnight UTC of the Rome date), so the
    /// first night of a stay is its check-in date and the night after is the next date. A date is a date whatever the daylight
    /// saving: the clock change of late March and late October moves no night from one day to another.
    /// </summary>
    public static bool IsWeekendNight(DateOnly night) => night.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;
}
