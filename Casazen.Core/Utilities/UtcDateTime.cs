namespace Casazen.Core.Utilities;

/// <summary>
/// Single rule for turning any incoming <see cref="DateTime"/> into UTC (FD-06).
/// PostgreSQL <c>timestamptz</c> columns only accept <see cref="DateTimeKind.Utc"/> values.
/// </summary>
public static class UtcDateTime
{
    /// <summary>
    /// <list type="bullet">
    /// <item><see cref="DateTimeKind.Utc"/>: returned unchanged.</item>
    /// <item><see cref="DateTimeKind.Unspecified"/>: the wall-clock value is taken as UTC
    /// (a date without time, e.g. "2026-10-01", becomes midnight UTC of that day, which is how
    /// stay and contract dates are stored).</item>
    /// <item><see cref="DateTimeKind.Local"/>: converted to UTC.</item>
    /// </list>
    /// </summary>
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
