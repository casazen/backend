using Casazen.Core.Enums;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// NF-33 (AC-NF33-2): every <see cref="PropertyAmenity"/> of the CasaZen catalog has an entry that says which amenity of the channel manager and
/// which Airbnb and Booking.com codes it stands for, so a new amenity without a decision fails here, as it fails
/// <c>PublicAmenityCatalogIntegrationTests</c> for the catalog.
/// </summary>
public class AmenityCatalogMapperTests
{
    [Fact]
    public void Entries_EveryPropertyAmenity_HasExactlyOneEntry()
    {
        Assert.Equal(
            Enum.GetValues<PropertyAmenity>().Order(),
            AmenityCatalogMapper.Entries.Select(entry => entry.Amenity).Order());
        Assert.All(
            Enum.GetValues<PropertyAmenity>(),
            amenity => Assert.Equal(amenity, AmenityCatalogMapper.Get(amenity).Amenity));
    }

    [Fact]
    public void Entries_AnAmenityOfTheChannelManager_StandsForOneAmenityOfCasaZenOnly()
    {
        var mapped = AmenityCatalogMapper.Entries.Where(entry => entry.OctorateId is not null).ToList();

        Assert.Equal(mapped.Count, mapped.Select(entry => entry.OctorateId).Distinct().Count());
        Assert.All(mapped, entry => Assert.False(string.IsNullOrWhiteSpace(entry.OctorateName)));
    }

    [Fact]
    public void Entries_APortalCodeBelongsToAnAmenityOfTheChannelManager_AndIsNeverBlank()
    {
        Assert.All(AmenityCatalogMapper.Entries, entry =>
        {
            Assert.True(entry.AirbnbCode is null || !string.IsNullOrWhiteSpace(entry.AirbnbCode));
            Assert.True(entry.BookingCode is null || !string.IsNullOrWhiteSpace(entry.BookingCode));
            if (entry.OctorateId is null)
            {
                Assert.Null(entry.OctorateName);
                Assert.Null(entry.AirbnbCode);
                Assert.Null(entry.BookingCode);
            }
        });
    }

    [Fact]
    public void NotInTheCatalog_AreTheAmenitiesTheChannelManagerHasNoCounterpartFor()
    {
        Assert.Equal(
            new[] { PropertyAmenity.BikeStorage, PropertyAmenity.SmokingAllowed, PropertyAmenity.StepFreeAccess }.Order(),
            AmenityCatalogMapper.NotInTheCatalog.Order());
    }

    [Fact]
    public void OctorateIds_GivesTheIdOfEachAmenityAskedOnce_InTheOrderOfTheCatalog_WithoutTheOnesThatHaveNoCounterpart()
    {
        var ids = AmenityCatalogMapper.OctorateIds(
            [PropertyAmenity.Kitchen, PropertyAmenity.BikeStorage, PropertyAmenity.WiFi, PropertyAmenity.Kitchen]);

        Assert.Equal([841L, 837L], ids);
    }
}
