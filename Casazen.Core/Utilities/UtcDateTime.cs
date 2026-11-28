namespace Casazen.Core.Utilities;

public static class UtcDateTime
{
    public static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// <paramref name="value"/> cut to whole microseconds, the precision of a PostgreSQL <c>timestamptz</c> (a .NET tick is a
    /// tenth of a microsecond): a value written to the database and read back is then equal to the one that was written, so
    /// the answer of a request and the answer of its replay carry the same instant.
    /// </summary>
    public static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % (TimeSpan.TicksPerMillisecond / 1000), value.Kind);
}
