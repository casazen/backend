using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-11a over HTTP: the public property page carries what the host said about the house and nothing else — a property the host never
/// described has an empty profile, a described one shows its facts, texts, labels (the host's and the ones its data give), rules and
/// embeds, the same through both routes, an arrival window turned upside down by a later change of the earliest time is not shown, and the
/// columns that hold the texts as stored never reach the page.
/// </summary>
public class PublicPropertyProfileIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;

    public PublicPropertyProfileIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task PublicPage_ShowsOnlyWhatTheHostFilledIn_ThroughBothRoutes_AndLeavesOutAnInvertedArrivalWindow()
    {
        var hostId = $"pubprofile-{Guid.NewGuid():N}";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);
        var property = await SeedPublishedPropertyAsync(hostId);
        using var anonymous = _factory.CreateClient();
        var bySlug = $"/api/public/orgs/{org.Slug}/properties/{property.Slug}";

        // Nothing said yet: nothing to show.
        var bare = (await anonymous.GetFromJsonAsync<JsonElement>(bySlug)).GetProperty("profile");
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("zone").ValueKind);
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("rules").ValueKind);
        Assert.Equal(JsonValueKind.Null, bare.GetProperty("video").ValueKind);
        Assert.Equal(0, bare.GetProperty("tags").GetArrayLength());
        Assert.Equal(0, bare.GetProperty("highlights").GetArrayLength());

        // The wizard's fields (the earliest arrival after the latest one the host gives below) and the site profile.
        await SetAsync(property.Id, p =>
        {
            p.Amenities = [PropertyAmenity.Pool];
            p.PetsPolicy = PetsPolicy.Yes;
            p.CheckInFrom = new TimeOnly(15, 0);
            p.CheckOutBy = new TimeOnly(10, 0);
            p.AreaSqm = 70;
        });
        using var host = _factory.CreateAuthenticatedClient(hostId, HostRole);
        var saved = await host.PutAsJsonAsync($"/api/properties/{property.Id}/site-profile", new
        {
            floorLevel = 1,
            energyClass = "C",
            childrenWelcome = true,
            checkInTo = "20:00",
            videoUrl = "https://youtu.be/dQw4w9WgXcQ",
            zone = new { it = "Centro di Lecce", en = "Lecce centre" },
            checkInMethod = new { it = "Con cassetta a codice" },
            highlights = new[] { new { icon = "sun", title = new { it = "Terrazza" } } },
            tags = new[] { "vista-mare" },
        });
        saved.EnsureSuccessStatusCode();

        var page = await anonymous.GetFromJsonAsync<JsonElement>(bySlug);
        var profile = page.GetProperty("profile");
        Assert.Equal(1, profile.GetProperty("floorLevel").GetInt32());
        Assert.Equal(70, profile.GetProperty("areaSqm").GetInt32());
        Assert.Equal("C", profile.GetProperty("energy").GetProperty("class").GetString());
        Assert.Equal("Lecce centre", profile.GetProperty("zone").GetProperty("en").GetString());
        Assert.Equal("Terrazza", profile.GetProperty("highlights")[0].GetProperty("title").GetProperty("it").GetString());
        // The host's label and the ones the data of the house give (a pool, pets welcome), in the order of the vocabulary.
        Assert.Equal(["piscina", "pet", "vista-mare"], profile.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("YouTube", profile.GetProperty("video").GetProperty("provider").GetString());
        Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", profile.GetProperty("video").GetProperty("url").GetString());

        var rules = profile.GetProperty("rules");
        Assert.Equal("15:00:00", rules.GetProperty("checkIn").GetProperty("from").GetString());
        Assert.Equal("20:00:00", rules.GetProperty("checkIn").GetProperty("to").GetString());
        Assert.Equal("10:00:00", rules.GetProperty("checkOutBy").GetString());
        Assert.Equal("Yes", rules.GetProperty("pets").GetString());
        Assert.True(rules.GetProperty("childrenWelcome").GetBoolean());

        // The other route gives the same profile; the stored columns of the texts are not part of the page.
        var byId = await anonymous.GetFromJsonAsync<JsonElement>($"/api/properties/{property.Id}/public");
        Assert.Equal(profile.GetRawText(), byId.GetProperty("profile").GetRawText());
        Assert.DoesNotContain("siteProfileJson", page.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photoAltJson", page.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The wizard moves the earliest arrival after the latest one: the pair is upside down and the page shows the earliest alone.
        await SetAsync(property.Id, p => p.CheckInFrom = new TimeOnly(21, 0));
        var inverted = (await anonymous.GetFromJsonAsync<JsonElement>(bySlug)).GetProperty("profile").GetProperty("rules").GetProperty("checkIn");
        Assert.Equal("21:00:00", inverted.GetProperty("from").GetString());
        Assert.Equal(JsonValueKind.Null, inverted.GetProperty("to").ValueKind);
    }

    private async Task<Property> SeedPublishedPropertyAsync(string hostId)
    {
        var seeded = await _factory.SeedPropertyAsync(hostId);
        await SetAsync(seeded.Id, p =>
        {
            p.Slug = $"casa-{Guid.NewGuid():N}"[..16];
            p.City = "Lecce";
            p.CinCode = "IT058091C27G5FFZDZ";
            p.ComplianceStatus = PropertyComplianceStatus.Active;
        });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == seeded.Id);
    }

    private async Task SetAsync(Guid propertyId, Action<Property> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
    }
}
