namespace Casazen.Core.Utilities;

public static class RomeCalendar
{
    public const string TimeZoneId = "Europe/Rome";

    private static readonly Lazy<TimeZoneInfo> Zone = new(() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo> PropertyZones = new(StringComparer.Ordinal);

    public static TimeZoneInfo TimeZone => Zone.Value;

    public static DateTime TodayInRome(this TimeProvider timeProvider) => TodayAt(timeProvider.GetUtcNow());

    public static DateOnly TodayInRomeAsDateOnly(this TimeProvider timeProvider) =>
        DateOnly.FromDateTime(TodayInRome(timeProvider));

    public static DateTime TodayAt(DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, TimeZone);
        return new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Utc);
    }

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

    public static DateTimeOffset ToRome(DateTime utc)
    {
        var instant = utc.Kind switch
        {
            DateTimeKind.Local => utc.ToUniversalTime(),
            _ => DateTime.SpecifyKind(utc, DateTimeKind.Utc),
        };
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(instant), TimeZone);
    }

    public static DateTime StartOfDayUtc(DateTime calendarDate)
    {
        var localMidnight = new DateTime(calendarDate.Year, calendarDate.Month, calendarDate.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, TimeZone);
    }

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
    public static DateTime ToUtc(DateOnly date, TimeOnly time) => ToUtc(date, time, TimeZone);

    public static DateTime ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(local))
        {
            var before = zone.GetUtcOffset(local.AddDays(-1));
            return DateTime.SpecifyKind(local - before, DateTimeKind.Utc);
        }

        if (zone.IsAmbiguousTime(local))
        {
            var summer = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - summer, DateTimeKind.Utc);
        }

        return DateTime.SpecifyKind(local - zone.GetUtcOffset(local), DateTimeKind.Utc);
    }

    public static TimeZoneInfo ZoneOf(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZone;

        return PropertyZones.GetOrAdd(
            timeZoneId.Trim(),
            id =>
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    return TimeZone;
                }
            });
    }

    public static DateTime ToUtc(DateOnly date, int minuteOfDay)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minuteOfDay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minuteOfDay, 24 * 60);

        return minuteOfDay == 24 * 60
            ? ToUtc(date.AddDays(1), TimeOnly.MinValue)
            : ToUtc(date, new TimeOnly(minuteOfDay / 60, minuteOfDay % 60));
    }
}
