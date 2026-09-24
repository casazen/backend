using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// TN-4 on the real pipeline and PostgreSQL: the tenant of a request follows the org linked during that request
/// (A1-20), parallel first accesses create one user and, without the onboarding, no org (A1-14, PL-02), and parallel
/// creates cannot exceed the plan limit (A1-21).
/// </summary>
public class TenantProvisioningConcurrencyPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public TenantProvisioningConcurrencyPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewSub() => $"auth0|tn4-{Guid.NewGuid():N}";

    [PostgresFact]
    public async Task GetOrProvisionOrgIdAsync_OrgLinkedAfterTheTenantWasResolved_TenantQueriesReturnItsRows()
    {
        var sub = NewSub();
        await using var requestScope = _factory.Services.CreateAsyncScope();
        var accessor = requestScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", sub), new Claim("email", $"{Guid.NewGuid():N}@example.com")], "Test")),
            RequestServices = requestScope.ServiceProvider,
        };

        try
        {
            // What TenantResolutionMiddleware does first: the brand-new user has no org yet.
            var tenant = requestScope.ServiceProvider.GetRequiredService<IRequestTenantContext>();
            await tenant.ResolveAsync();
            Assert.Null(tenant.OrgId);

            // Meanwhile a parallel request (the onboarding with its consents) creates and links the org.
            var linkedOrg = await _factory.SeedOrgForOwnerAsync(sub);

            var orgId = await requestScope.ServiceProvider.GetRequiredService<IOrgContextResolver>()
                .GetOrProvisionOrgIdAsync();
            Assert.Equal(linkedOrg.Id, orgId);

            var db = requestScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Properties.Add(NewProperty(sub, linkedOrg.Id, "Via Stessa Richiesta 1"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Same request, tenant-filtered read: it must see the row of the org linked in the meantime (A1-20).
            var visible = await db.Properties.Where(p => p.OwnerId == sub).ToListAsync();

            Assert.Equal(linkedOrg.Id, tenant.OrgId);
            var property = Assert.Single(visible);
            Assert.Equal(linkedOrg.Id, property.OrgId);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [PostgresFact]
    public async Task GetOrProvisionOrgIdAsync_UserWithoutOrg_ReturnsNullAndCreatesNoOrg()
    {
        // PL-02 (A1-05): the first org of a user is created only by the onboarding, with the legal consents.
        var sub = NewSub();
        await using var requestScope = _factory.Services.CreateAsyncScope();
        var accessor = requestScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "Test")),
            RequestServices = requestScope.ServiceProvider,
        };

        try
        {
            await requestScope.ServiceProvider.GetRequiredService<IRequestTenantContext>().ResolveAsync();

            var orgId = await requestScope.ServiceProvider.GetRequiredService<IOrgContextResolver>()
                .GetOrProvisionOrgIdAsync();

            Assert.Null(orgId);
            var db = requestScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = Assert.Single(await db.Users.AsNoTracking().Where(u => u.Id == sub).ToListAsync());
            Assert.Null(user.OrgId);
            Assert.Equal(UserRole.None, user.Role);
            Assert.Equal(0, await db.Orgs.CountAsync(o => o.Slug.StartsWith($"org-{sub.Replace("|", "-")}")));
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [PostgresFact]
    public async Task FirstAccess_ParallelRequestsWithoutOnboarding_OneUserNoOrgAndHostEndpointsRefused()
    {
        var sub = NewSub();
        var client = _factory.CreateAuthenticatedClient(
            userId: sub, roles: "PropertyOwner", email: $"{Guid.NewGuid():N}@example.com");
        string[] paths =
        [
            "/api/orgs/me/entitlement", "/api/users/me", "/api/orgs/me/entitlement",
            "/api/orgs/me/entitlement", "/api/users/me", "/api/orgs/me/entitlement",
        ];

        var responses = await Task.WhenAll(paths.Select(path => client.GetAsync(path)));

        for (var i = 0; i < paths.Length; i++)
        {
            var body = await responses[i].Content.ReadAsStringAsync();
            if (paths[i] == "/api/users/me")
            {
                Assert.True(responses[i].StatusCode == HttpStatusCode.OK, $"{paths[i]}: {(int)responses[i].StatusCode} {body}");
                continue;
            }

            // A JWT role alone is not a completed onboarding (PL-02): no org is provisioned behind the user's back.
            Assert.True(responses[i].StatusCode == HttpStatusCode.Forbidden, $"{paths[i]}: {(int)responses[i].StatusCode} {body}");
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("onboarding_required", doc.RootElement.GetProperty("code").GetString());
        }

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = Assert.Single(await db.Users.AsNoTracking().Where(u => u.Id == sub).ToListAsync());
        Assert.Null(user.OrgId);
        Assert.Equal(UserRole.None, user.Role);
        var baseSlug = $"org-{sub.Replace("|", "-")}";
        Assert.Equal(0, await db.Orgs.CountAsync(o => o.Slug.StartsWith(baseSlug)));
    }

    [PostgresFact]
    public async Task CreateProperty_TwoParallelRequestsWithOneSlotLeft_OneCreatedAndOne403PlanLimitReached()
    {
        var owner = NewSub();
        // Starter allows 3 properties: 2 seeded, one slot left.
        var seeded = await _factory.SeedPropertyAsync(ownerId: owner);
        await _factory.SeedPropertyAsync(ownerId: owner);
        var client = _factory.CreateAuthenticatedClient(userId: owner, roles: "PropertyOwner");

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/properties", PropertyBody("Casa Parallela Uno", "Via Parallela 1")),
            client.PostAsJsonAsync("/api/properties", PropertyBody("Casa Parallela Due", "Via Parallela 2")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        using var doc = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("plan_limit_reached", doc.RootElement.GetProperty("code").GetString());

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.Properties.CountAsync(p => p.OrgId == seeded.OrgId));
    }

    private static object PropertyBody(string name, string address) => new
    {
        name,
        address,
        city = "Rome",
        bedrooms = 2,
        bathrooms = 1,
        maxGuests = 4,
        nightlyRate = 90m,
    };

    private static Property NewProperty(string ownerId, Guid orgId, string address) => new()
    {
        OwnerId = ownerId,
        OrgId = orgId,
        Name = "Proprietà nuova org",
        Description = "TN-4",
        Address = address,
        City = "Rome",
        PostalCode = "00100",
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 80m,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
}
