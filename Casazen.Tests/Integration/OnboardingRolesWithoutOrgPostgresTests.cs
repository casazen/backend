using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A1-01 on real PostgreSQL: a host whose Auth0 roles were assigned by hand (so the JWT has roles) but who has no org
/// yet completes the onboarding through the API instead of hitting a 400 dead end. <c>PUT</c> and <c>POST</c> are
/// idempotent: the first call with the consents creates the org, every later call (and a parallel one) links the
/// same org.
/// </summary>
public class OnboardingRolesWithoutOrgPostgresTests(CasazenWebApplicationFactory factory)
    : IClassFixture<CasazenWebApplicationFactory>
{
    private const string ConsentVersion = "2026-06-v1";
    private const string OnboardingPath = "/api/users/onboarding";

    [PostgresFact]
    public async Task Onboarding_HostWithRolesWithoutOrg_CreatesOrgOnceAndRepeatsIdempotently()
    {
        var userId = NewUserId();
        using var client = factory.CreateAuthenticatedClient(userId, roles: "PropertyOwner");

        // First login: the user row exists, the org does not.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/users/me");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("orgId").ValueKind);

        // PUT without consents (the old client path for users with roles): explicit code, nothing provisioned.
        var withoutConsents = await client.PutAsJsonAsync(OnboardingPath, new { rentalType = "ShortTerm" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withoutConsents.StatusCode);
        var problem = await withoutConsents.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("consents_required", problem.GetProperty("code").GetString());
        Assert.Equal(0, await CountOrgsOfAsync(userId));

        // PUT with consents: creates the org and records the consents.
        var first = await ReadOkAsync(await client.PutAsJsonAsync(OnboardingPath, Payload("ShortTerm")));
        var orgId = first.GetProperty("orgId").GetString();
        Assert.True(first.GetProperty("orgProvisioned").GetBoolean());
        Assert.True(first.GetProperty("consentsRecorded").GetBoolean());

        // Repeated PUT (no consents needed any more) and POST: same org, nothing provisioned again.
        var again = await ReadOkAsync(await client.PutAsJsonAsync(OnboardingPath, new { rentalType = "ShortTerm" }));
        Assert.Equal(orgId, again.GetProperty("orgId").GetString());
        Assert.False(again.GetProperty("orgProvisioned").GetBoolean());
        Assert.False(again.GetProperty("consentsRecorded").GetBoolean());

        var post = await ReadOkAsync(await client.PostAsJsonAsync(OnboardingPath, Payload("ShortTerm")));
        Assert.Equal(orgId, post.GetProperty("orgId").GetString());
        Assert.False(post.GetProperty("orgProvisioned").GetBoolean());

        var me = await client.GetFromJsonAsync<JsonElement>("/api/users/me");
        Assert.Equal(orgId, me.GetProperty("orgId").GetString());
        Assert.Equal("ShortTerm", me.GetProperty("rentalType").GetString());
        Assert.NotEqual(JsonValueKind.Null, me.GetProperty("onboardingCompletedAt").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await CountOrgsOfAsync(userId));
        Assert.True(await db.UserContextMemberships.AnyAsync(m => m.UserId == userId && m.ContextKey == "short-rent"));
    }

    [PostgresFact]
    public async Task Onboarding_HostWithRolesWithoutOrg_ParallelRequestsLinkOneOrg()
    {
        var userId = NewUserId();
        using var client = factory.CreateAuthenticatedClient(userId, roles: "LongTermLandlord");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/users/me")).StatusCode);

        // Double click, or the web and a second tab completing the wizard at the same time.
        var responses = await Task.WhenAll(
            client.PutAsJsonAsync(OnboardingPath, Payload("LongTerm")),
            client.PostAsJsonAsync(OnboardingPath, Payload("LongTerm")));

        var bodies = await Task.WhenAll(responses.Select(ReadOkAsync));
        Assert.Single(bodies.Select(b => b.GetProperty("orgId").GetString()).Distinct());
        Assert.Equal(1, await CountOrgsOfAsync(userId));
    }

    private static string NewUserId() => $"auth0|pl01-{Guid.NewGuid():N}";

    private async Task<int> CountOrgsOfAsync(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var slugPrefix = $"org-{userId.Replace("|", "-")}";
        return await db.Orgs.CountAsync(o => o.Slug.StartsWith(slugPrefix));
    }

    private static async Task<JsonElement> ReadOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static object Payload(string rentalType) => new
    {
        rentalType,
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
    };
}
