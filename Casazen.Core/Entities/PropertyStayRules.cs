namespace Casazen.Core.Entities;

public static class PropertyStayRules
{
    public const int MinMinNights = 1;

    public const int MaxMinNights = 30;

    public const decimal MaxWeekendSurchargePercent = 100m;

    public const int WeekendSurchargeScale = 2;

    public const string MinNightsInvalidCode = "property_min_nights_invalid";

    public const string WeekendSurchargeInvalidCode = "property_weekend_surcharge_invalid";

    public static bool IsValidMinNights(int? minNights) => minNights is null or (>= MinMinNights and <= MaxMinNights);

    public static bool IsValidWeekendSurcharge(decimal percent) => percent is >= 0m and <= MaxWeekendSurchargePercent;

    public static decimal NormalizeWeekendSurcharge(decimal percent) =>
        Math.Round(percent, WeekendSurchargeScale, MidpointRounding.AwayFromZero);

    public static bool IsWeekendNight(DateOnly night) => night.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday;
}
