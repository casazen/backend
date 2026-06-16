using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

public class PlgOnboardingIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-v1";
    private readonly CasazenWebApplicationFactory _factory;

    public PlgOnboardingIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC4_LegalEndpoints_AreAnonymous()
    {
        var client = _factory.CreateClient();

        var subprocessors = await client.GetAsync("/api/legal/subprocessors");
        Assert.Equal(HttpStatusCode.OK, subprocessors.StatusCode);

        var dpa = await client.GetAsync("/api/legal/dpa");
        Assert.Equal(HttpStatusCode.OK, dpa.StatusCode);

        var tos = await client.GetAsync("/api/legal/tos");
        Assert.Equal(HttpStatusCode.OK, tos.StatusCode);

        var privacy = await client.GetAsync("/api/legal/privacy");
        Assert.Equal(HttpStatusCode.OK, privacy.StatusCode);

        var body = await subprocessors.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(ConsentVersion, body.GetProperty("version").GetString());
        Assert.True(body.GetProperty("items").GetArrayLength() >= 4);
    }

    [Fact]
    public async Task AC1_PostOnboarding_WithoutConsents_Returns400()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|plg-no-consent-{Guid.NewGuid():N}", roles: string.Empty);
        var response = await client.PostAsJsonAsync("/api/users/onboarding", new { rentalType = "ShortTerm" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC2_PostOnboarding_StaleConsentVersion_Returns400()
    {
        using var client = _factory.CreateAuthenticatedClient($"auth0|plg-stale-{Guid.NewGuid():N}", roles: string.Empty);
        var payload = BuildOnboardingPayload("ShortTerm", consents: new
        {
            tosAccepted = true,
            tosVersion = "old-version",
            privacyAccepted = true,
            privacyVersion = ConsentVersion,
            dpaAccepted = true,
            dpaVersion = ConsentVersion,
            subprocessorsAcknowledged = true,
            subprocessorsVersion = ConsentVersion,
        });
        var response = await client.PostAsJsonAsync("/api/users/onboarding", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC1_AC3_PostOnboarding_RecordsConsentsAndProvisionsOrg()
    {
        var userId = $"auth0|plg-success-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");

        var response = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("orgProvisioned").GetBoolean());
        Assert.True(body.GetProperty("consentsRecorded").GetBoolean());
        Assert.NotEqual(Guid.Empty.ToString(), body.GetProperty("orgId").GetString());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orgId = Guid.Parse(body.GetProperty("orgId").GetString()!);
        var records = await db.ConsentRecords.IgnoreQueryFilters()
            .Where(c => c.UserId == userId && c.OrgId == orgId)
            .ToListAsync();

        Assert.Equal(4, records.Count);
        Assert.All(records, r => Assert.Equal("203.0.113.10", r.IpAddress));
        Assert.Contains(records, r => r.Type == ConsentType.Tos && r.Version == ConsentVersion);
    }

    [Fact]
    public async Task AC5_GetOnboardingStatus_RequiresAuth()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/onboarding/status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC5_AC6_GetOnboardingStatus_ReflectsActivationMilestones()
    {
        var userId = $"auth0|plg-status-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var onboard = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, onboard.StatusCode);

        var statusResponse = await client.GetAsync("/api/onboarding/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);

        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(status.GetProperty("roleChosen").GetBoolean());
        Assert.True(status.GetProperty("orgProvisioned").GetBoolean());
        Assert.True(status.GetProperty("consentsAccepted").GetBoolean());
        Assert.False(status.GetProperty("propertyCreated").GetBoolean());
        Assert.False(status.GetProperty("activated").GetBoolean());
    }

    [Fact]
    public async Task AC7_PutOnboarding_DoesNotRequireConsents()
    {
        var userId = $"auth0|plg-put-{Guid.NewGuid():N}";
        using var client = _factory.CreateAuthenticatedClient(userId, roles: string.Empty);

        var post = await client.PostAsJsonAsync("/api/users/onboarding", BuildOnboardingPayload("ShortTerm"));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var put = await client.PutAsJsonAsync("/api/users/onboarding", new { rentalType = "LongTerm" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("orgProvisioned").GetBoolean());
        Assert.False(body.GetProperty("consentsRecorded").GetBoolean());
    }

    private static object BuildOnboardingPayload(string rentalType, object? consents = null) => new
    {
        rentalType,
        consents = consents ?? ValidConsents(),
    };

    private static object ValidConsents() => new
    {
        tosAccepted = true,
        tosVersion = ConsentVersion,
        privacyAccepted = true,
        privacyVersion = ConsentVersion,
        dpaAccepted = true,
        dpaVersion = ConsentVersion,
        subprocessorsAcknowledged = true,
        subprocessorsVersion = ConsentVersion,
    };
}
