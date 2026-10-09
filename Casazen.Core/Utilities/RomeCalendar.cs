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

    /// <summary>
    /// Europe/Rome calendar date of a stored or received date value. A date-only value is midnight UTC of its date
    /// (storage convention), which is the same date in Rome; an instant with a time (e.g. <c>2026-01-31T23:30Z</c>) is
    /// converted, so it gives the Rome date (<c>2026-02-01</c>). <see cref="DateTimeKind.Unspecified"/> counts as UTC.
    /// </summary>
    public static DateOnly DateInRome(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => value,
        };
        return DateOnly.FromDateTime(TodayAt(new DateTimeOffset(utc)));
    }

    /// <summary>
    /// The UTC instant at which the calendar date of <paramref name="calendarDate"/> (a date-only value, e.g. a
    /// check-in date) starts in Europe/Rome: 22:00 or 23:00 UTC of the day before, depending on daylight saving.
    /// </summary>
    public static DateTime StartOfDayUtc(DateTime calendarDate)
    {
        var localMidnight = new DateTime(calendarDate.Year, calendarDate.Month, calendarDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, TimeZone);
    }

    /// <summary>
    /// The UTC instant at which <paramref name="date"/> starts in Europe/Rome (<see cref="ToUtc(DateOnly, TimeOnly)"/> of
    /// midnight, which is never skipped nor repeated by the daylight saving change).
    /// </summary>
    public static DateTime StartOfDayUtc(DateOnly date) => ToUtc(date, TimeOnly.MinValue);

    /// <summary>
    /// The UTC instant of the Europe/Rome wall-clock time <paramref name="time"/> on <paramref name="date"/> (SP-03: working
    /// hours are wall-clock times of Rome, 09:00 stays 09:00 all year, and the slots of a date are the UTC instants those
    /// hours are on that date). The result has <see cref="DateTimeKind.Utc"/>.
    /// </summary>
    /// <remarks>
    /// The two days a year the clock changes have a rule, so a time never has two answers or none:
    /// <list type="bullet">
    /// <item><b>A time that does not exist</b> (the hour skipped when summer time starts, on the last Sunday of March:
    /// 02:00-02:59 on 29 March 2026) is read with the offset in force <i>before</i> the change (+01:00), which puts it one
    /// hour later on the clock: 02:30 is 03:30 summer time, the instant 01:30 UTC. Same as <c>java.time</c> and the
    /// <c>compatible</c> choice of Temporal.</item>
    /// <item><b>A time that happens twice</b> (the hour repeated when summer time ends, on the last Sunday of October:
    /// 02:00-02:59 on 25 October 2026) is its <i>first</i> occurrence, the one in summer time (+02:00): 02:30 is the
    /// instant 00:30 UTC, not 01:30 UTC.</item>
    /// </list>
    /// Every other time maps to the single instant it names. Midnight is never skipped nor repeated, so
    /// <see cref="StartOfDayUtc(DateOnly)"/> is exact.
    /// </remarks>
    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (TimeZone.IsInvalidTime(local))
        {
            // Skipped hour: the offset the day had before the change, taken from the day before (no change happens twice
            // in 24 hours). The wall-clock time then lands after the gap.
            var before = TimeZone.GetUtcOffset(local.AddDays(-1));
            return DateTime.SpecifyKind(local - before, DateTimeKind.Utc);
        }

        if (TimeZone.IsAmbiguousTime(local))
        {
            // Repeated hour: the first occurrence is the earlier instant, the one with the larger offset (summer time).
            var summer = TimeZone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - summer, DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(local - TimeZone.GetUtcOffset(local), DateTimeKind.Utc);
    }

    /// <summary>
    /// <see cref="ToUtc(DateOnly, TimeOnly)"/> of a time given as minutes after midnight, 0 to 1440; 1440 is the midnight
    /// that ends <paramref name="date"/> (the end of a working band that runs until midnight).
    /// </summary>
    public static DateTime ToUtc(DateOnly date, int minuteOfDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minuteOfDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minuteOfDay, 24 * 60);

        return minuteOfDay == 24 * 60
            ? ToUtc(date.AddDays(1), TimeOnly.MinValue)
            : ToUtc(date, new TimeOnly(minuteOfDay / 60, minuteOfDay % 60));
    }
}
