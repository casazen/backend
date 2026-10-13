using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AppContextEntity = Casazen.Core.Entities.AppContext;

namespace Casazen.Tests.Integration;

/// <summary>
/// QA-INFRA-01: the web hosts of the integration tests never share data and start with the reference data the migrations seed.
/// On PostgreSQL (CI) that is true by construction, one migrated database per factory; these tests hold the InMemory fallback
/// of a local run, without a server, to the same rules: it used to be one store for the whole process, so hosts starting
/// together imported the comuni sample into the same rows ("An item with the same key has already been added. Key: 001235"),
/// a test saw what other classes had written, and no context, role or permission existed. They run on both providers.
/// </summary>
public class IntegrationHostIsolationTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const int HostsStartedTogether = 3;

    [Fact]
    public async Task Hosts_StartedTogether_EachLoadsTheComuniSampleOnceIntoItsOwnDatabase()
    {
        var hosts = Enumerable.Range(0, HostsStartedTogether).Select(_ => new CasazenWebApplicationFactory()).ToList();
        try
        {
            // The host is built and started on first use: all of them at the same time, as xUnit starts test classes in parallel.
            await Task.WhenAll(hosts.Select(host => Task.Run(() => host.Services)));

            var comuniOfEachHost = new List<string[]>();
            foreach (var host in hosts)
            {
                using var scope = host.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                comuniOfEachHost.Add(await db.Comuni.OrderBy(c => c.IstatCode).Select(c => c.IstatCode).ToArrayAsync());
            }

            Assert.NotEmpty(comuniOfEachHost[0]);
            Assert.All(comuniOfEachHost, codes => Assert.Equal(codes.Distinct().Count(), codes.Length));
            Assert.All(comuniOfEachHost, codes => Assert.Equal(comuniOfEachHost[0], codes));

            // What one host writes the others do not see.
            const string slug = "test-org-isolation-check";
            using (var scope = hosts[0].Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Orgs.Add(new OrgEntity { Name = "Isolation", Slug = slug, DisplayName = "Isolation", IsActive = true });
                await db.SaveChangesAsync();
            }

            foreach (var other in hosts.Skip(1))
            {
                using var scope = other.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                Assert.False(await db.Orgs.IgnoreQueryFilters().AnyAsync(o => o.Slug == slug));
            }
        }
        finally
        {
            foreach (var host in hosts)
                await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Host_Database_HasTheContextsRolesAndPermissionsOfTheModelSeed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var model = db.GetService<IDesignTimeModel>().Model;

        var seededContexts = model.FindEntityType(typeof(AppContextEntity))!.GetSeedData().Select(row => (string)row["Key"]!).ToList();
        var seededRoles = model.FindEntityType(typeof(Role))!.GetSeedData()
            .Select(row => (Id: (int)row["Id"]!, ContextKey: (string)row["ContextKey"]!, RoleKey: (string)row["RoleKey"]!)).ToList();
        var seededPermissions = model.FindEntityType(typeof(RolePermission))!.GetSeedData()
            .Select(row => (RoleId: (int)row["RoleId"]!, PermissionKey: (string)row["PermissionKey"]!)).ToList();

        // The seed is not empty (a model without HasData would make this pass for nothing).
        Assert.Contains("short-rent", seededContexts);
        Assert.NotEmpty(seededRoles);
        Assert.NotEmpty(seededPermissions);

        var contexts = await db.AppContexts.Select(c => c.Key).ToListAsync();
        var roles = (await db.Roles.Select(r => new { r.Id, r.ContextKey, r.RoleKey }).ToListAsync())
            .Select(r => (r.Id, r.ContextKey, r.RoleKey)).ToList();
        var permissions = (await db.RolePermissions.Select(p => new { p.RoleId, p.PermissionKey }).ToListAsync())
            .Select(p => (p.RoleId, p.PermissionKey)).ToList();

        Assert.Empty(seededContexts.Except(contexts));
        Assert.Empty(seededRoles.Except(roles));
        Assert.Empty(seededPermissions.Except(permissions));
    }
}
