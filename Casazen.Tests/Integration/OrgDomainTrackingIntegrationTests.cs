using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E40/F01/US01/T04 (NF-149): the Google Tag Manager container of an agency. The endpoint is hidden while <c>SiteEmbed</c> is off, saves only the
/// caller's own org, refuses anything that is not a container id without echoing it, and <c>resolve-host</c> serves the saved id at once (and
/// nothing while the flag is off).
/// </summary>
public class OrgDomainTrackingIntegrationTests(CasazenWebApplicationFactory flagsOff, SiteIntegrationFlagsFactory flagsOn)
    : IClassFixture<CasazenWebApplicationFactory>, IClassFixture<SiteIntegrationFlagsFactory>
{
    [Fact]
    public async Task PutTracking_FlagOff_NotFound()
    {
        var ownerId = $"auth0|tracking-off-{Guid.NewGuid():N}";
        var org = await flagsOff.SeedOrgForOwnerAsync(ownerId);

        using var client = flagsOff.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/tracking", new { gtmContainerId = "GTM-ABC123" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOff, org.Id)).GtmContainerId);
    }

    [Fact]
    public async Task PutTracking_OtherOrgId_Forbidden()
    {
        var victimId = $"auth0|tracking-victim-{Guid.NewGuid():N}";
        var attackerId = $"auth0|tracking-attacker-{Guid.NewGuid():N}";
        var victim = await flagsOn.SeedOrgForOwnerAsync(victimId);
        await flagsOn.SeedOrgForOwnerAsync(attackerId);

        using var client = flagsOn.CreateAuthenticatedClient(attackerId, "PropertyOwner");
        var response = await client.PutAsJsonAsync($"/api/orgs/{victim.Id}/domain/tracking", new { gtmContainerId = "GTM-EVIL123" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, victim.Id)).GtmContainerId);
    }

    [Fact]
    public async Task PutTracking_SavesTheNormalizedId_AndNullClearsIt()
    {
        var ownerId = $"auth0|tracking-save-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");

        var saved = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/tracking", new { gtmContainerId = " gtm-abc123 " });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("GTM-ABC123", (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gtmContainerId").GetString());
        Assert.Equal("GTM-ABC123", (await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).GtmContainerId);
        var settings = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");
        Assert.Equal("GTM-ABC123", settings.GetProperty("tracking").GetProperty("gtmContainerId").GetString());

        var cleared = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/tracking", new { gtmContainerId = (string?)null });

        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await cleared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("gtmContainerId").ValueKind);
        Assert.Null((await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).GtmContainerId);
    }

    [Theory]
    [InlineData("it", "non è valido")]
    [InlineData("en", "is not valid")]
    public async Task PutTracking_InvalidId_BadRequestInTheLanguageOfTheRequest_WithoutEchoingIt(string language, string words)
    {
        var ownerId = $"auth0|tracking-invalid-{Guid.NewGuid():N}";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(flagsOn, org.Id, o => o.GtmContainerId = "GTM-KEEP01");
        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/orgs/{org.Id}/domain/tracking")
        {
            Content = JsonContent.Create(new { gtmContainerId = "GTM-<script>alert(1)</script>" }),
        };
        request.Headers.AcceptLanguage.ParseAdd(language);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("gtm_container_id_invalid", problem.RootElement.GetProperty("code").GetString());
        Assert.Contains(words, problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("script", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("GTM-KEEP01", (await OrgDomainSurfacesIntegrationTests.ReadOrgAsync(flagsOn, org.Id)).GtmContainerId);
    }

    [Fact]
    public async Task ResolveHost_ServesTheSavedId_AndASaveDropsTheCachedAnswer()
    {
        var ownerId = $"auth0|tracking-resolve-{Guid.NewGuid():N}";
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await flagsOn.SeedOrgForOwnerAsync(ownerId);
        await OrgDomainSurfacesIntegrationTests.SetPlanAsync(flagsOn, org.Id, PlanTier.Pro);
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOn, org.Id, domain);
        using var publicClient = flagsOn.CreateClient();
        var before = await publicClient.GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("tracking").GetProperty("gtmContainerId").ValueKind);

        using var client = flagsOn.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var saved = await client.PutAsJsonAsync($"/api/orgs/{org.Id}/domain/tracking", new { gtmContainerId = "GTM-XYZ789" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var after = await publicClient.GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        Assert.Equal("GTM-XYZ789", after.GetProperty("tracking").GetProperty("gtmContainerId").GetString());
        Assert.True(after.GetProperty("embedEnabled").GetBoolean());
    }

    [Fact]
    public async Task ResolveHost_FlagOff_TrackingNull()
    {
        var ownerId = $"auth0|tracking-resolve-off-{Guid.NewGuid():N}";
        var domain = $"www.{Guid.NewGuid():N}.example.test";
        var org = await flagsOff.SeedOrgForOwnerAsync(ownerId);
        await OrgDomainSurfacesIntegrationTests.SetPlanAsync(flagsOff, org.Id, PlanTier.Pro);
        await OrgDomainSurfacesIntegrationTests.SetVerifiedDomainAsync(flagsOff, org.Id, domain);
        await OrgDomainSurfacesIntegrationTests.UpdateOrgAsync(flagsOff, org.Id, o => o.GtmContainerId = "GTM-STORED1");

        var resolved = await flagsOff.CreateClient().GetFromJsonAsync<JsonElement>($"/api/public/resolve-host?host={domain}");
        using var client = flagsOff.CreateAuthenticatedClient(ownerId, "PropertyOwner");
        var settings = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{org.Id}/domain");

        Assert.Equal(JsonValueKind.Null, resolved.GetProperty("tracking").GetProperty("gtmContainerId").ValueKind);
        Assert.False(resolved.GetProperty("embedEnabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("tracking").ValueKind);
    }
}
