namespace Casazen.Core.Utilities;

public static class TimezoneHelper
{
    public static DateTime ConvertUtcToLocal(DateTime utcDateTime, string timezoneId)
    {
        if (utcDateTime.Kind != DateTimeKind.Utc)
            throw new ArgumentException("DateTime must be in UTC", nameof(utcDateTime));

        var timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, timezone);
    }

    public static DateTime ConvertLocalToUtc(DateTime localDateTime, string timezoneId)
    {
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        return TimeZoneInfo.ConvertTimeToUtc(localDateTime, timezone);
    }

    public static int GetUtcOffsetMinutes(string timezoneId, DateTime atTime)
    {
        var timezone = TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        var offset = timezone.GetUtcOffset(atTime);
        return (int)offset.TotalMinutes;
    }

    public static bool IsValidTimezone(string timezoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }
}
