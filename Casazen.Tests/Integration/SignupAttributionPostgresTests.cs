using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SE-03 (A8-03) on real PostgreSQL: the web app sends the attribution captured on <c>/signup</c> after the first
/// onboarding. It is stored once per org (the first one wins, retries and parallel calls do not overwrite it), a value
/// outside the rules is refused (400, or 422 for an unknown comune) and nothing is stored, and the platform admin can
/// read it back.
/// </summary>
public class SignupAttributionPostgresTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    private const string AttributionPath = "/api/users/me/signup-attribution";
    private const string ConsentVersion = "2026-06-v1";
    private const string ComoIstatCode = "013075";

    [PostgresFact]
    public async Task RecordSignupAttribution_AfterFirstOnboarding_StoresTheFirstAttributionOnlyOnce()
    {
        var (client, orgId) = await OnboardedHostAsync();
        using var _ = client;

        var first = await client.PostAsJsonAsync(AttributionPath, new
        {
            utmSource = "seo-compliance",
            utmMedium = "cta",
            utmCampaign = "estate 2026",
            utmContent = "compliance-guide",
            comune = "como",
            landingPath = "/p/affitti-brevi/lombardia/como",
            referrerHost = "www.google.com",
        });
        Assert.True((await ReadOkAsync(first)).GetProperty("recorded").GetBoolean());

        // A retry of the web app, or a later signup link of another campaign: the first attribution stays.
        var second = await client.PostAsJsonAsync(AttributionPath, new
        {
            utmSource = "newsletter",
            comune = "roma",
            landingPath = "/signup",
        });
        Assert.False((await ReadOkAsync(second)).GetProperty("recorded").GetBoolean());

        var stored = Assert.Single(await AttributionsOfAsync(orgId));
        Assert.Equal("seo-compliance", stored.UtmSource);
        Assert.Equal("cta", stored.UtmMedium);
        Assert.Equal("estate 2026", stored.UtmCampaign);
        Assert.Null(stored.UtmTerm);
        Assert.Equal("compliance-guide", stored.UtmContent);
        Assert.Equal(ComoIstatCode, stored.ComuneCode);
        Assert.Equal("/p/affitti-brevi/lombardia/como", stored.LandingPath);
        Assert.Equal("www.google.com", stored.ReferrerHost);
        Assert.Equal(DateTimeKind.Utc, stored.RecordedAt.Kind);
    }

    [PostgresFact]
    public async Task RecordSignupAttribution_ParallelRequests_StoreOneRow()
    {
        var (client, orgId) = await OnboardedHostAsync();
        using var _ = client;

        // Two tabs finishing the onboarding at the same time.
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
            client.PostAsJsonAsync(AttributionPath, new { utmSource = $"source-{i}", comune = "como" })));

        var recorded = new List<bool>();
        foreach (var response in responses)
            recorded.Add((await ReadOkAsync(response)).GetProperty("recorded").GetBoolean());

        Assert.Single(recorded, r => r);
        Assert.Single(await AttributionsOfAsync(orgId));
    }

    [PostgresFact]
    public async Task RecordSignupAttribution_InvalidValues_Returns400AndStoresNothing()
    {
        var (client, orgId) = await OnboardedHostAsync();
        using var _ = client;

        object[] invalid =
        [
            new { utmSource = new string('a', 101) },               // longer than 100 characters
            new { utmCampaign = "mario.rossi@example.com" },          // "@": an email address never passes
            new { utmTerm = "<script>" },                             // markup
            new { comune = "Como" },                                  // slugs are lower case
            new { landingPath = "/p/affitti-brevi?email=a@b.it" },   // no query string in the path
            new { landingPath = "p/affitti-brevi" },                  // a path of the web app starts with "/"
            new { landingPath = "/" + new string('a', 200) },         // longer than 200 characters
            new { referrerHost = "https://www.google.com/search" },   // host only, no scheme or path
        ];

        foreach (var body in invalid)
        {
            var response = await client.PostAsJsonAsync(AttributionPath, body);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"Expected 400 for {JsonSerializer.Serialize(body)}");
            var raw = await response.Content.ReadAsStringAsync();
            var problem = JsonSerializer.Deserialize<JsonElement>(raw);
            Assert.Equal("validation_error", problem.GetProperty("code").GetString());
            // The field error is the localized message, never the resource key.
            Assert.NotEqual(JsonValueKind.Undefined, problem.GetProperty("errors").ValueKind);
            Assert.DoesNotContain("SignupAttribution", raw, StringComparison.Ordinal);
        }

        // Well-formed but not a comune CasaZen knows: refused as a business rule, the rest of the body is not stored.
        var unknownComune = await client.PostAsJsonAsync(AttributionPath, new { utmSource = "google", comune = "atlantide" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownComune.StatusCode);
        var unknownProblem = await unknownComune.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("signup_attribution_unknown_comune", unknownProblem.GetProperty("code").GetString());

        Assert.Empty(await AttributionsOfAsync(orgId));

        // Refused values are not stored "half": a valid attribution afterwards is still the first one.
        var valid = await client.PostAsJsonAsync(AttributionPath, new { utmSource = "google", comune = ComoIstatCode });
        Assert.True((await ReadOkAsync(valid)).GetProperty("recorded").GetBoolean());
        Assert.Equal(ComoIstatCode, Assert.Single(await AttributionsOfAsync(orgId)).ComuneCode);
    }

    [PostgresFact]
    public async Task RecordSignupAttribution_BeforeOnboarding_Returns422()
    {
        var userId = NewUserId();
        using var client = factory.CreateAuthenticatedClient(userId);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);

        var response = await client.PostAsJsonAsync(AttributionPath, new { utmSource = "google" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("signup_attribution_onboarding_required", problem.GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task ListSignupAttributions_Admin_ReadsEveryOrgAndHostIsRefused()
    {
        var (hostClient, orgId) = await OnboardedHostAsync();
        using var _ = hostClient;
        await ReadOkAsync(await hostClient.PostAsJsonAsync(AttributionPath, new { utmSource = "seo-compliance", comune = "como" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await hostClient.GetAsync("/api/admin/signup-attributions")).StatusCode);

        using var adminClient = factory.CreateAuthenticatedClient($"auth0|se03-admin-{Guid.NewGuid():N}", roles: "Admin");
        var page = await ReadOkAsync(await adminClient.GetAsync($"/api/admin/signup-attributions?comuneCode={ComoIstatCode}&pageSize=100"));

        var item = page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("orgId").GetGuid() == orgId);
        Assert.Equal("seo-compliance", item.GetProperty("utmSource").GetString());
        Assert.Equal(ComoIstatCode, item.GetProperty("comuneCode").GetString());
        Assert.Equal("Como", item.GetProperty("comuneName").GetString());
    }

    private async Task<(HttpClient Client, Guid OrgId)> OnboardedHostAsync()
    {
        var client = factory.CreateAuthenticatedClient(NewUserId());
        var onboarding = await ReadOkAsync(await client.PostAsJsonAsync("/api/users/onboarding", new
        {
            rentalType = "ShortTerm",
            consents = new
            {
                tosAccepted = true,
                tosVersion = ConsentVersion,
                privacyAccepted = true,
                privacyVersion = ConsentVersion,
                dpaAccepted = true,
                dpaVersion = ConsentVersion,
                subprocessorsAcknowledged = true,
                subprocessorsVersion = ConsentVersion,
            },
        }));

        Assert.True(onboarding.GetProperty("orgProvisioned").GetBoolean());
        return (client, onboarding.GetProperty("orgId").GetGuid());
    }

    private async Task<List<SignupAttribution>> AttributionsOfAsync(Guid orgId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Test assertion outside any request: read the rows of the org directly.
        return await db.SignupAttributions.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrgId == orgId).ToListAsync();
    }

    private static string NewUserId() => $"auth0|se03-{Guid.NewGuid():N}";

    private static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
