using Casazen.Core.OfficialData;
using Xunit;

namespace Casazen.Tests.Unit.OfficialData;

public class MefPublicationCalendarTests
{
    [Fact]
    public void ValidFrom_CesanoMadernoPublication_IsFirstDayOfSecondFollowingMonth()
    {
        Assert.Equal(new DateOnly(2025, 3, 1), MefPublicationCalendar.ValidFrom(new DateOnly(2025, 1, 10)));
    }

    [Theory]
    [InlineData(2025, 11, 30, 2026, 1, 1)]
    [InlineData(2025, 12, 1, 2026, 2, 1)]
    public void ValidFrom_YearBoundary(int y, int m, int d, int ey, int em, int ed) =>
        Assert.Equal(new DateOnly(ey, em, ed), MefPublicationCalendar.ValidFrom(new DateOnly(y, m, d)));
}
