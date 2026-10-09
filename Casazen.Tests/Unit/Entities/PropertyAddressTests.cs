using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Entities;

/// <summary>PC-06 (A2-19, A2-33): the unit as stored, and the range and precision of the coordinates.</summary>
public class PropertyAddressTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("int. 5", "int. 5")]
    [InlineData("  int. 5  ", "int. 5")]
    [InlineData("Scala  B \t int.   5", "Scala B int. 5")]
    [InlineData("INT 5", "INT 5")]
    public void NormalizeUnit_Value_IsTrimmedWithSpacesCollapsedAndBlankIsNull(string? input, string? expected)
    {
        Assert.Equal(expected, PropertyAddress.NormalizeUnit(input));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(-90, true)]
    [InlineData(45.464211, true)]
    [InlineData(90.000001, false)]
    [InlineData(-91, false)]
    [InlineData(1234.56, false)]
    public void IsValidLatitude_Value_AcceptsOnlyMinus90To90(double latitude, bool expected)
    {
        Assert.Equal(expected, PropertyAddress.IsValidLatitude((decimal)latitude));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(180, true)]
    [InlineData(-180, true)]
    [InlineData(9.189982, true)]
    [InlineData(180.000001, false)]
    [InlineData(-200, false)]
    public void IsValidLongitude_Value_AcceptsOnlyMinus180To180(double longitude, bool expected)
    {
        Assert.Equal(expected, PropertyAddress.IsValidLongitude((decimal)longitude));
    }

    [Fact]
    public void RoundCoordinate_MoreThanSixDecimals_KeepsSixHalfAwayFromZero()
    {
        Assert.Equal(41.902783m, PropertyAddress.RoundCoordinate(41.9027825m));
        Assert.Equal(-41.902783m, PropertyAddress.RoundCoordinate(-41.9027825m));
        Assert.Equal(12.496366m, PropertyAddress.RoundCoordinate(12.4963660001m));
    }

    [Fact]
    public void RoundCoordinate_SixDecimals_IsNotChanged()
    {
        // The old column kept 2 decimals (about 1 km): a point such as 41.902782 must come back as it was sent.
        Assert.Equal(41.902782m, PropertyAddress.RoundCoordinate(41.902782m));
    }
}
