using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Enums;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-11a: the booking site names, groups and draws every amenity from one reference list served to everybody. A new
/// <see cref="PropertyAmenity"/> without an entry would reach the property page as a raw code, so this fails until it has one.
/// </summary>
public class PublicAmenityCatalogIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public PublicAmenityCatalogIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_Anonymous_GivesEveryAmenityOnceWithItsLabelsIconAndAGroupThatExists()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/public/amenities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var groups = body.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("id").GetString()).ToList();
        var amenities = body.GetProperty("amenities").EnumerateArray().ToList();

        // Every amenity of the enum, exactly once, under its name (what the public property DTOs carry).
        Assert.Equal(
            Enum.GetNames<PropertyAmenity>().Order(StringComparer.Ordinal),
            amenities.Select(a => a.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        Assert.Equal(["comfort", "kitchen", "bedbath", "outdoor", "safety"], groups);
        Assert.All(amenities, a =>
        {
            Assert.Contains(a.GetProperty("group").GetString(), groups);
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("icon").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("label").GetProperty("it").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(a.GetProperty("label").GetProperty("en").GetString()));
        });
    }
}
