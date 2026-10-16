using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-04e: the arithmetic behind two of the three numbers of the console home (SP-D13): how often the supplier says yes, and how long it
/// takes to answer. The numbers on real requests are in <c>SupplierOverviewMetricsTests</c>.
/// </summary>
public class SupplierOverviewRulesTests
{
    [Theory]
    [InlineData(9, 1, 0.9)]
    [InlineData(1, 1, 0.5)]
    [InlineData(1, 0, 1.0)]
    [InlineData(0, 1, 0.0)]
    [InlineData(2, 1, 0.6667)]
    [InlineData(1, 2, 0.3333)]
    [InlineData(1, 6, 0.1429)]
    public void Rate_IsTheRequestsTakenOverTheRequestsAnswered_WithFourDecimals(int accepted, int rejected, double expected)
    {
        Assert.Equal(expected, SupplierAcceptance.Rate(accepted, rejected));
    }

    [Fact]
    public void Rate_WhenNothingWasAnswered_IsNull_NeverZeroPercent()
    {
        Assert.Null(SupplierAcceptance.Rate(0, 0));
    }

    [Fact]
    public void Rate_ANegativeCount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierAcceptance.Rate(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SupplierAcceptance.Rate(0, -1));
    }

    [Fact]
    public void AverageMinutes_IsTheMeanInWholeMinutes()
    {
        Assert.Equal(18, SupplierResponseTimes.AverageMinutes([10, 20, 25])); // 18.33
        Assert.Equal(20, SupplierResponseTimes.AverageMinutes([20]));
        Assert.Equal(0, SupplierResponseTimes.AverageMinutes([0.2, 0.4])); // under half a minute
    }

    [Fact]
    public void AverageMinutes_WithNoAnswer_IsNull_NeverZeroMinutes()
    {
        Assert.Null(SupplierResponseTimes.AverageMinutes([]));
    }

    [Fact]
    public void AverageMinutes_IsNotTheMedianOfTheShowcase()
    {
        // One request left for a weekend moves the home's average (the supplier's own view), not the public median (SP-09).
        double[] minutes = [5, 6, 7, 8, 3000];

        Assert.Equal(605, SupplierResponseTimes.AverageMinutes(minutes));
        Assert.Equal(7, SupplierResponseTimes.PublicMedianMinutes(minutes));
    }
}
