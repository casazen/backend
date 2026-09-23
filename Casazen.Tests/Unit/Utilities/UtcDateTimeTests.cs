using System.Globalization;
using Casazen.Core.Utilities;
using Xunit;

namespace Casazen.Tests.Unit.Utilities;

public class UtcDateTimeTests
{
    [Theory]
    [InlineData(DateTimeKind.Unspecified, "2026-10-01T00:00:00")]
    [InlineData(DateTimeKind.Utc, "2026-10-01T00:00:00")]
    public void Normalize_UnspecifiedOrUtc_KeepsWallClockAsUtc(DateTimeKind kind, string expected)
    {
        var normalized = UtcDateTime.Normalize(new DateTime(2026, 10, 1, 0, 0, 0, kind));

        Assert.Equal(DateTime.Parse(expected, CultureInfo.InvariantCulture), normalized);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }

    [Fact]
    public void Normalize_Local_ConvertsToUtcInstant()
    {
        var local = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Local);

        var normalized = UtcDateTime.Normalize(local);

        Assert.Equal(local.ToUniversalTime(), normalized);
        Assert.Equal(DateTimeKind.Utc, normalized.Kind);
    }
}
