namespace Casazen.Core.Utilities;

/// <summary>
/// Calendar "today" for hosts, guests and properties (Italian market, Europe/Rome) (FD-06).
/// Use it wherever a date is compared with the user's or the property's calendar day
/// (check-in/check-out reached, "not in the past", days to a deadline, default date ranges).
/// Technical timestamps (CreatedAt, token expiries, job windows) keep using the UTC instant.
/// </summary>
public static class RomeCalendar
{
    public const string TimeZoneId = "Europe/Rome";

    private static readonly Lazy<TimeZoneInfo> Zone = new(() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

    public static TimeZoneInfo TimeZone => Zone.Value;

    /// <summary>
    /// Today's calendar date in Europe/Rome, as midnight UTC of that date
    /// (the storage convention for date-only values such as check-in or contract dates).
    /// </summary>
    public static DateTime TodayInRome(this TimeProvider timeProvider) => TodayAt(timeProvider.GetUtcNow());

    /// <summary>Today's calendar date in Europe/Rome as a <see cref="DateOnly"/>.</summary>
    public static DateOnly TodayInRomeAsDateOnly(this TimeProvider timeProvider) =>
        DateOnly.FromDateTime(TodayInRome(timeProvider));

    /// <summary>
    /// The Europe/Rome calendar date of <paramref name="instant"/>, as midnight UTC of that date.
    /// </summary>
    public static DateTime TodayAt(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, TimeZone);
        return new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Utc);
    }
}
