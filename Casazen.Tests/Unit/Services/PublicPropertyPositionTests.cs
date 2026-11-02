using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The position of a property as the public maps use it (DB-03): the one the host gave, to about a kilometre; for a host who gave none
/// (<c>0, 0</c> = "not set"), the centre of the comune of the property; nothing invented otherwise.
/// </summary>
public class PublicPropertyPositionTests
{
    [Fact]
    public void Of_NoPositionAndAKnownComune_IsTheCentreOfTheComuneToAboutAKilometre()
    {
        // Como: the centre in the seed is 45.8103, 9.0861.
        var (latitude, longitude) = PublicPropertyPosition.Of(0m, 0m, "013075");

        Assert.Equal(45.81m, latitude);
        Assert.Equal(9.09m, longitude);
    }

    [Fact]
    public void Of_APositionOfTheHost_IsTheirsToAboutAKilometreWhateverTheComuneSays()
    {
        var (latitude, longitude) = PublicPropertyPosition.Of(45.464211m, 9.191383m, "013075");

        Assert.Equal(45.46m, latitude);
        Assert.Equal(9.19m, longitude);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("999999")]
    public void Of_NoPositionAndNoKnownComune_StaysZeroZero(string? comuneIstatCode)
    {
        var (latitude, longitude) = PublicPropertyPosition.Of(0m, 0m, comuneIstatCode);

        Assert.Equal(0m, latitude);
        Assert.Equal(0m, longitude);
    }

    [Theory]
    [InlineData(0, 9.1)] // on the equator
    [InlineData(51.48, 0)] // on the meridian of Greenwich
    public void Of_OnlyOneOfTheTwoIsZero_IsARealPositionAndIsKept(double latitude, double longitude)
    {
        var (shownLatitude, shownLongitude) = PublicPropertyPosition.Of((decimal)latitude, (decimal)longitude, "013075");

        Assert.Equal((decimal)latitude, shownLatitude);
        Assert.Equal((decimal)longitude, shownLongitude);
    }
}
