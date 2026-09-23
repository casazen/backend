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
}
