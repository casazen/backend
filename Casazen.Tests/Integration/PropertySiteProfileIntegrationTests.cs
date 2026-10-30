using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// DB-11a over HTTP: the host saves the site profile of a property (<c>PUT api/properties/{id}/site-profile</c>) and reads it back. The
/// save replaces the profile, stores it clean and in canonical form, refuses a mistake with the stable code and the field and saves
/// nothing, and it is the host's own property only: another org's property does not exist, a colleague without ownership is refused,
/// an anonymous caller is not let in, and a property let for the long term has no page to describe.
/// </summary>
public class PropertySiteProfileIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string HostRole = "PropertyOwner";

    private readonly CasazenWebApplicationFactory _factory;

    public PropertySiteProfileIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewUser() => $"auth0|siteprofile-{Guid.NewGuid():N}";

    private static string UrlOf(Guid propertyId) => $"/api/properties/{propertyId}/site-profile";

    [Fact]
    public async Task Put_StoresTheWholeProfileCleanAndCanonical_GetReadsItBack_AndASecondSaveWithNothingClearsIt()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        await SetAsync(property.Id, p =>
        {
            p.Amenities = [PropertyAmenity.Pool, PropertyAmenity.PetFriendly];
        });
        using var client = _factory.CreateAuthenticatedClient(ownerId, HostRole);

        var saved = await client.PutAsJsonAsync(UrlOf(property.Id), new
        {
            floorLevel = 1,
            renovatedYear = 2021,
            energyClass = "d",
            energyIndex = 148.4,
            areaKind = "Country",
            childrenWelcome = true,
            checkInTo = "20:00",
            videoUrl = "https://youtu.be/dQw4w9WgXcQ?si=abc",
            zone = new { it = "  Campagna di Ostuni ", en = "Countryside near Ostuni" },
            highlights = new[] { new { icon = "waves", title = new { it = "Piscina privata", en = "Private pool" } } },
            safetyNotes = new[] { new { it = "Estintore" } },
            tags = new[] { "vista-mare" },
        });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var body = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("D", body.GetProperty("energyClass").GetString());
        Assert.Equal(148.4m, body.GetProperty("energyIndex").GetDecimal());
        Assert.Equal("Country", body.GetProperty("areaKind").GetString());
        Assert.Equal("20:00:00", body.GetProperty("checkInTo").GetString());
        Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", body.GetProperty("videoUrl").GetString());
        Assert.Equal("YouTube", body.GetProperty("videoProvider").GetString());
        Assert.Equal("Campagna di Ostuni", body.GetProperty("zone").GetProperty("it").GetString());
        Assert.Equal(["vista-mare"], body.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        // What the data of the house give it: a pool, pets welcome, the countryside.
        Assert.Equal(["piscina", "pet", "campagna"], body.GetProperty("derivedTags").EnumerateArray().Select(t => t.GetString()));

        // Read back exactly as saved, and stored in the columns of the property.
        var read = await client.GetFromJsonAsync<JsonElement>(UrlOf(property.Id));
        Assert.Equal(Without(body, "updatedAt"), Without(read, "updatedAt"));
        var stored = await LoadAsync(property.Id);
        Assert.Equal("D", stored.EnergyClass);
        Assert.Equal("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", stored.VideoUrl);
        Assert.NotNull(stored.SiteProfileJson);

        // A save replaces: what it leaves out is cleared, and a profile with nothing leaves the column empty.
        var cleared = await client.PutAsJsonAsync(UrlOf(property.Id), new { });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        var after = await LoadAsync(property.Id);
        Assert.Null(after.SiteProfileJson);
        Assert.Null(after.EnergyClass);
        Assert.Null(after.VideoUrl);
        Assert.Null(after.FloorLevel);
        // The data of the house are not the profile: they stay, and so do their labels.
        var view = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["piscina", "pet"], view.GetProperty("derivedTags").EnumerateArray().Select(t => t.GetString()));
    }

    [Fact]
    public async Task Put_ARuleBroken_Is422WithTheStableCodeAndTheField_InTheLanguageOfTheCaller_AndTheStoredProfileIsUntouched()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var client = _factory.CreateAuthenticatedClient(ownerId, HostRole);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(UrlOf(property.Id), new { zone = new { it = "Prima" }, floorLevel = 2 })).StatusCode);

        // A video that is not on the allow-list.
        var video = await client.PutAsJsonAsync(
            UrlOf(property.Id), new { zone = new { it = "Dopo" }, videoUrl = "https://evil.example/watch?v=dQw4w9WgXcQ" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, video.StatusCode);
        var problem = await video.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("property_site_profile_video_url_invalid", problem.GetProperty("code").GetString());
        Assert.Equal("videoUrl", problem.GetProperty("field").GetString());

        // An English text with no Italian one, asked in English: the message is the English one.
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var english = await client.PutAsJsonAsync(UrlOf(property.Id), new { near = new { en = "Ten minutes from the sea" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, english.StatusCode);
        var englishProblem = await english.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("property_site_profile_italian_required", englishProblem.GetProperty("code").GetString());
        Assert.Equal("near", englishProblem.GetProperty("field").GetString());
        Assert.Contains("Italian", englishProblem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Nothing of the two refused saves was kept.
        var view = await client.GetFromJsonAsync<JsonElement>(UrlOf(property.Id));
        Assert.Equal("Prima", view.GetProperty("zone").GetProperty("it").GetString());
        Assert.Equal(2, view.GetProperty("floorLevel").GetInt32());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("videoUrl").ValueKind);
    }

    [Fact]
    public async Task Access_OnlyTheHostsOwnShortRentProperty_AnotherOrgIs404_AColleagueIs403_AnonymousIs401_AndALongRentPropertyIs422()
    {
        var ownerId = NewUser();
        var property = await _factory.SeedPropertyAsync(ownerId);
        using var owner = _factory.CreateAuthenticatedClient(ownerId, HostRole);
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PutAsJsonAsync(UrlOf(property.Id), new { zone = new { it = "Mia zona" } })).StatusCode);

        // Another org: the tenant filter hides the property, so the caller cannot learn that it exists.
        var strangerId = NewUser();
        await _factory.SeedOrgForOwnerAsync(strangerId);
        using var stranger = _factory.CreateAuthenticatedClient(strangerId, HostRole);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(UrlOf(property.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync(UrlOf(property.Id), new { zone = new { it = "Rubata" } })).StatusCode);

        // A colleague of the same org who does not own the property and holds no org-wide role.
        var colleagueId = await SeedColleagueAsync(property.OrgId);
        using var colleague = _factory.CreateAuthenticatedClient(colleagueId, HostRole);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.GetAsync(UrlOf(property.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await colleague.PutAsJsonAsync(UrlOf(property.Id), new { zone = new { it = "Collega" } })).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(UrlOf(property.Id))).StatusCode);

        Assert.Equal("Mia zona", ZoneOf(await LoadAsync(property.Id)));

        // A property let for the long term has no page on the booking site.
        await SetAsync(property.Id, p => p.RentalMode = RentalMode.Long);
        var longMode = await owner.PutAsJsonAsync(UrlOf(property.Id), new { zone = new { it = "Lungo periodo" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longMode.StatusCode);
        Assert.Equal("property_not_bookable_in_long_mode", (await longMode.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal("Mia zona", ZoneOf(await LoadAsync(property.Id)));
    }

    private static string? ZoneOf(Property property) =>
        SitePropertyProfileJson.Parse(property.SiteProfileJson).Zone?.It;

    private static string Without(JsonElement element, string name)
    {
        var node = JsonNode.Parse(element.GetRawText())!.AsObject();
        node.Remove(name);
        return node.ToJsonString();
    }

    private async Task<Property> LoadAsync(Guid propertyId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId);
    }

    private async Task SetAsync(Guid propertyId, Action<Property> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        change(property);
        await db.SaveChangesAsync();
    }

    private async Task<string> SeedColleagueAsync(Guid orgId)
    {
        var colleagueId = NewUser();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = colleagueId,
            Email = $"{Guid.NewGuid():N}@example.com",
            FirstName = "Collega",
            LastName = "Stessa Org",
            OrgId = orgId,
            IsActive = true,
        };
        db.Users.Add(user);
        // Onboarded for the org (PL-02): the 403 of this test is about ownership, not about the onboarding gate.
        await HostOnboardingSeed.MarkOnboardedAsync(db, user, orgId, scope.ServiceProvider.GetRequiredService<ILegalDocumentService>());
        await db.SaveChangesAsync();
        return colleagueId;
    }
}
