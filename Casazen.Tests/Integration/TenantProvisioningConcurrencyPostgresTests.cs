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
/// TN-4 on the real pipeline and PostgreSQL: the tenant of a request follows the org provisioned in that
/// request (A1-20), parallel first accesses create one user and one org (A1-14), and parallel creates cannot
/// exceed the plan limit (A1-21).
/// </summary>
public class TenantProvisioningConcurrencyPostgresTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public TenantProvisioningConcurrencyPostgresTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private static string NewSub() => $"auth0|tn4-{Guid.NewGuid():N}";

    [PostgresFact]
    public async Task GetOrProvisionOrgIdAsync_OrgProvisionedInTheSameRequest_TenantQueriesReturnItsRows()
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

            var orgId = await requestScope.ServiceProvider.GetRequiredService<IOrgContextResolver>()
                .GetOrProvisionOrgIdAsync();
            Assert.NotNull(orgId);

            var db = requestScope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Properties.Add(NewProperty(sub, orgId.Value, "Via Stessa Richiesta 1"));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Same request, tenant-filtered read: it must see the row of the org just provisioned.
            var visible = await db.Properties.Where(p => p.OwnerId == sub).ToListAsync();

            Assert.Equal(orgId, tenant.OrgId);
            var property = Assert.Single(visible);
            Assert.Equal(orgId, property.OrgId);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [PostgresFact]
    public async Task FirstAccess_ParallelRequests_AllReturn200WithOneUserAndOneOrg()
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

        foreach (var response in responses)
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = Assert.Single(await db.Users.AsNoTracking().Where(u => u.Id == sub).ToListAsync());
        Assert.NotNull(user.OrgId);
        var baseSlug = $"org-{sub.Replace("|", "-")}";
        Assert.Equal(1, await db.Orgs.CountAsync(o => o.Slug.StartsWith(baseSlug)));

        var entitlementOrgIds = new List<Guid>();
        for (var i = 0; i < paths.Length; i++)
        {
            if (paths[i] != "/api/orgs/me/entitlement")
                continue;
            using var doc = JsonDocument.Parse(await responses[i].Content.ReadAsStringAsync());
            entitlementOrgIds.Add(doc.RootElement.GetProperty("orgId").GetGuid());
        }

        Assert.All(entitlementOrgIds, id => Assert.Equal(user.OrgId, id));
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
