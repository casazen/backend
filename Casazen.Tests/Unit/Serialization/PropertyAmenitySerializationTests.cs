using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Enums;
using Xunit;

namespace Casazen.Tests.Unit.Serialization;

public class PropertyAmenitySerializationTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("WiFi", PropertyAmenity.WiFi)]
    [InlineData("AirConditioning", PropertyAmenity.AirConditioning)]
    [InlineData("Washer", PropertyAmenity.Washer)]
    [InlineData("BBQGrill", PropertyAmenity.BBQGrill)]
    [InlineData("FirstAidKit", PropertyAmenity.FirstAidKit)]
    [InlineData("PetFriendly", PropertyAmenity.PetFriendly)]
    [InlineData("CarbonMonoxideDetector", PropertyAmenity.CarbonMonoxideDetector)]
    [InlineData("FireExtinguisher", PropertyAmenity.FireExtinguisher)]
    [InlineData("FreeParking", PropertyAmenity.FreeParking)]
    [InlineData("Terrace", PropertyAmenity.Terrace)]
    public void Deserialize_StringValue_ReturnsCorrectEnumMember(string json, PropertyAmenity expected)
    {
        var result = JsonSerializer.Deserialize<PropertyAmenity>($"\"{json}\"", _options);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(PropertyAmenity.WiFi, "WiFi")]
    [InlineData(PropertyAmenity.AirConditioning, "AirConditioning")]
    [InlineData(PropertyAmenity.Terrace, "Terrace")]
    public void Serialize_EnumMember_ReturnsStringName(PropertyAmenity amenity, string expected)
    {
        var result = JsonSerializer.Serialize(amenity, _options);

        Assert.Equal($"\"{expected}\"", result);
    }

    [Fact]
    public void Deserialize_AmenityList_RoundTrip_Succeeds()
    {
        var amenities = new List<PropertyAmenity>
        {
            PropertyAmenity.WiFi,
            PropertyAmenity.AirConditioning,
            PropertyAmenity.Terrace,
            PropertyAmenity.FreeParking,
        };

        var json = JsonSerializer.Serialize(amenities, _options);
        var result = JsonSerializer.Deserialize<List<PropertyAmenity>>(json, _options);

        Assert.Equal(amenities, result);
    }

    [Fact]
    public void Deserialize_InvalidStringValue_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PropertyAmenity>("\"NonExistentAmenity\"", _options));
    }
}
