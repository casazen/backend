using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E07/F06/US01/T01 (NF-95, AC-NF95-8, Q1-29): the unfinished marketing module is behind <c>Features:CrmMarketing</c>, off by default. Off,
/// every endpoint of the CRM and of the campaigns answers 404 like a route that does not exist, before authentication, so nothing of it can be
/// reached; the flag is exposed to the frontend by <c>GET /api/public/features</c>. The public consent, confirmation and unsubscribe endpoints are
/// <b>not</b> behind it: a link already sent keeps working whatever the flag says.
/// </summary>
public class CrmMarketingFeatureFlagTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    /// <summary>
    /// Proof test (permission rule): while the flag is off nothing of the unfinished module is reachable, not even by an anonymous caller who
    /// would otherwise be asked for a token (404, never 401). The segments, the campaigns and their sending, and the CRM profiles.
    /// </summary>
    [Theory]
    [InlineData("GET", "/api/campaigns")]
    [InlineData("GET", "/api/campaigns/segments")]
    [InlineData("POST", "/api/campaigns/segments")]
    [InlineData("POST", "/api/campaigns")]
    [InlineData("GET", "/api/campaigns/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/campaigns/00000000-0000-0000-0000-000000000001/submit")]
    [InlineData("POST", "/api/campaigns/00000000-0000-0000-0000-000000000001/schedule")]
    [InlineData("GET", "/api/crm/guests")]
    [InlineData("GET", "/api/crm/guests/00000000-0000-0000-0000-000000000001")]
    [InlineData("PUT", "/api/crm/guests/00000000-0000-0000-0000-000000000001/tags")]
    [InlineData("GET", "/api/crm/merge-candidates")]
    [InlineData("POST", "/api/crm/merge-candidates/00000000-0000-0000-0000-000000000001/dismiss")]
    [InlineData("POST", "/api/crm/merge-candidates/00000000-0000-0000-0000-000000000001/merge")]
    public async Task FeatureGate_CrmMarketingOff_CampaignsAnswer404_Test(string method, string path)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request(method, path));

        await OtaPartnerApiFeatureFlagTests.AssertNotFoundProblemAsync(response);
    }

    [Fact]
    public async Task CrmMarketingEndpoints_FlagOn_AreReached_SoTheAuthenticationAsksForTheToken()
    {
        await using var enabled = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:CrmMarketing"] = "true" })));
        using var client = enabled.CreateClient();

        var campaigns = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request("GET", "/api/campaigns"));
        var crm = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request("GET", "/api/crm/guests"));

        Assert.Equal(HttpStatusCode.Unauthorized, campaigns.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, crm.StatusCode);
    }

    [Theory]
    [InlineData("/api/public/marketing/unsubscribe?orgId=00000000-0000-0000-0000-000000000001", "email_hash_missing")]
    [InlineData("/api/public/marketing/confirm-email?guestId=00000000-0000-0000-0000-000000000001", "email_missing")]
    public async Task PublicMarketingEndpoints_AreNotBehindTheFlag_TheyAnswerTheirOwnProblems(string path, string code)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(OtaPartnerApiFeatureFlagTests.Request("POST", path));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task PublicFeatures_Default_ReturnsCrmMarketingOff()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("crmMarketing").GetBoolean());
    }

    [Fact]
    public async Task PublicFeatures_FlagOn_ReturnsCrmMarketingOn()
    {
        await using var enabled = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:CrmMarketing"] = "true" })));
        using var client = enabled.CreateClient();

        var response = await client.GetAsync("/api/public/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("crmMarketing").GetBoolean());
    }
}
