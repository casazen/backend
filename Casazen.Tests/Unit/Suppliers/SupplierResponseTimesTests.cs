using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-09: the response time the public page may show (<see cref="SupplierResponseTimes"/>): the median of the minutes the
/// requests waited, only with at least <see cref="PublicShowcaseLimits.ResponseTimeMinSamples"/> of them, never a value
/// written by hand.
/// </summary>
public class SupplierResponseTimesTests
{
    [Fact]
    public void PublicMedianMinutes_NoRequests_IsNotShown()
    {
        Assert.Null(SupplierResponseTimes.PublicMedianMinutes([]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void PublicMedianMinutes_FewerThanFiveRequests_IsNotShown(int count)
    {
        Assert.Null(SupplierResponseTimes.PublicMedianMinutes(Enumerable.Repeat(10d, count).ToList()));
    }

    [Fact]
    public void PublicMedianMinutes_ExactlyFiveRequests_IsTheMiddleOne()
    {
        Assert.Equal(5, PublicShowcaseLimits.ResponseTimeMinSamples);
        Assert.Equal(30, SupplierResponseTimes.PublicMedianMinutes([50d, 10d, 30d, 20d, 40d]));
    }

    [Fact]
    public void PublicMedianMinutes_OneRequestLeftForAWeekend_DoesNotMoveIt_AnAverageWouldBe()
    {
        // Four answers in minutes and one after three days: the median is the typical one, the average (about 14 hours) is not.
        var minutes = new[] { 10d, 12d, 15d, 20d, 4320d };

        Assert.Equal(15, SupplierResponseTimes.PublicMedianMinutes(minutes));
        Assert.True(minutes.Average() > 800);
    }

    [Fact]
    public void PublicMedianMinutes_AnEvenNumber_IsTheMiddleOfTheTwoCentralOnes_AHalfMinuteRoundsUp()
    {
        Assert.Equal(4, SupplierResponseTimes.PublicMedianMinutes([1d, 2d, 3d, 4d, 5d, 6d])); // (3 + 4) / 2 = 3.5
        Assert.Equal(3, SupplierResponseTimes.PublicMedianMinutes([1d, 2d, 3d, 3d, 4d, 5d])); // (3 + 3) / 2
    }

    [Fact]
    public void PublicMedianMinutes_IsWholeMinutes()
    {
        Assert.Equal(7, SupplierResponseTimes.PublicMedianMinutes([1d, 2d, 7.4d, 9d, 12d]));
        Assert.Equal(8, SupplierResponseTimes.PublicMedianMinutes([1d, 2d, 7.5d, 9d, 12d]));
    }

    [Fact]
    public void PublicMedianMinutes_ANegativeWait_IsReadAsZero()
    {
        // A clock that moved: taken before it was received.
        Assert.Equal(0, SupplierResponseTimes.PublicMedianMinutes([-5d, -1d, 0d, 3d, 9d]));
    }

    [Fact]
    public void PublicMedianMinutes_DoesNotDependOnTheOrder()
    {
        var ordered = new[] { 5d, 10d, 20d, 40d, 80d, 160d, 320d };

        Assert.Equal(
            SupplierResponseTimes.PublicMedianMinutes(ordered),
            SupplierResponseTimes.PublicMedianMinutes(ordered.Reverse().ToArray()));
        Assert.Equal(40, SupplierResponseTimes.PublicMedianMinutes(ordered));
    }
}
