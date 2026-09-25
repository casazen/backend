using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-05 data migration (<see cref="SeparateSupplierOrgFromHostOrgId"/>) on real PostgreSQL (A1-40): a
/// supplier-only account's <c>OrgId</c> is detached from its Supplier org, unless that org already holds host
/// business data, in which case it is left for a manual product decision (docs/runbooks/suppliers.md §13).
/// </summary>
public class SeparateSupplierOrgFromHostOrgIdMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    [PostgresFact]
    public async Task BackfillSql_SupplierOrgWithoutHostData_ClearsOrgIdAndKeepsSupplierLink()
    {
        var supplierOrg = NewOrg("clean-supplier", OrgType.Supplier);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(supplierOrg);
            seed.Users.Add(NewUser("clean-supplier", u =>
            {
                u.OrgId = supplierOrg.Id;
                u.SupplierOrgId = supplierOrg.Id;
            }));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("clean-supplier");
        Assert.Null(user.OrgId);
        Assert.Equal(supplierOrg.Id, user.SupplierOrgId);
    }

    [PostgresFact]
    public async Task BackfillSql_LegacyUserWithoutSupplierOrgIdSet_BackfillsSupplierLinkThenClearsOrgId()
    {
        var supplierOrg = NewOrg("legacy-supplier", OrgType.Supplier);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(supplierOrg);
            // Pre-SU-08 shape: only OrgId was ever set, SupplierOrgId stayed null.
            seed.Users.Add(NewUser("legacy-supplier", u => u.OrgId = supplierOrg.Id));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("legacy-supplier");
        Assert.Null(user.OrgId);
        Assert.Equal(supplierOrg.Id, user.SupplierOrgId);
    }

    [PostgresFact]
    public async Task BackfillSql_SupplierOrgWithHostProperty_LeavesTheLinkForManualRepair()
    {
        // A1-40 ran to completion for this user before the fix: a property was created under the Supplier org.
        var supplierOrg = NewOrg("host-data-supplier", OrgType.Supplier);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(supplierOrg);
            var owner = NewUser("host-data-supplier", u =>
            {
                u.OrgId = supplierOrg.Id;
                u.SupplierOrgId = supplierOrg.Id;
            });
            seed.Users.Add(owner);
            seed.Properties.Add(new Property
            {
                OwnerId = owner.Id,
                OrgId = supplierOrg.Id,
                Name = "Casa nell'org fornitore",
                Address = $"Via Migrazione {Guid.NewGuid():N}",
                City = "Roma",
            });
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("host-data-supplier");
        Assert.Equal(supplierOrg.Id, user.OrgId);
        Assert.Equal(supplierOrg.Id, user.SupplierOrgId);
        await using var check = NewContext();
        Assert.True(await check.Properties.IgnoreQueryFilters().AnyAsync(p => p.OrgId == supplierOrg.Id));
    }

    [PostgresFact]
    public async Task BackfillSql_HostUserAndUserWithoutOrg_AreLeftUntouched()
    {
        var hostOrg = NewOrg("host", OrgType.Host);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(hostOrg);
            seed.Users.AddRange(
                NewUser("host-user", u => u.OrgId = hostOrg.Id),
                NewUser("no-org"));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var hostUser = await GetUserAsync("host-user");
        Assert.Equal(hostOrg.Id, hostUser.OrgId);
        Assert.Null(hostUser.SupplierOrgId);
        var noOrgUser = await GetUserAsync("no-org");
        Assert.Null(noOrgUser.OrgId);
        Assert.Null(noOrgUser.SupplierOrgId);
    }

    private async Task RunMigrationSqlAsync()
    {
        await using var migrate = NewContext();
        await migrate.Database.ExecuteSqlRawAsync(SeparateSupplierOrgFromHostOrgId.BackfillSql);
    }

    private async Task<User> GetUserAsync(string key)
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == $"auth0|mig-{key}");
    }

    private static Casazen.Core.Entities.Org NewOrg(string name, OrgType orgType) => new()
    {
        Name = $"Org {name}",
        Slug = $"mig-{name}-{Guid.NewGuid():N}",
        DisplayName = $"Org {name}",
        ContactEmail = $"{name}@example.com",
        OrgType = orgType,
    };

    private static User NewUser(string key, Action<User>? configure = null)
    {
        var user = new User
        {
            Id = $"auth0|mig-{key}",
            Email = $"{key}.{Guid.NewGuid():N}@example.com",
            FirstName = "Migrazione",
            LastName = key,
            Role = UserRole.Supplier,
            IsActive = true,
        };
        configure?.Invoke(user);
        return user;
    }
}
