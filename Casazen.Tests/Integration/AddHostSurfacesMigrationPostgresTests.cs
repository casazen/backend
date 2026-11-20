using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// E40/F01/US01/T01 on real PostgreSQL: the <c>AddHostSurfaces</c> migration adds the five columns of the surfaces of the agency's own
/// domain with their defaults, so the orgs that exist keep working exactly as before: guest area and owner portal off, the links on the
/// domain (they take effect only once the domain is verified and a surface is on), the Auth0 state <c>NotRequested</c>, no GTM container.
/// The row is written with SQL that names only the columns of the first <c>Orgs</c> table (<see cref="LegacyOrgRows"/>).
/// </summary>
public class AddHostSurfacesMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_ExistingOrg_GetsTheDefaultsOfTheSurfaces()
    {
        var id = Guid.NewGuid();
        await using (var db = _database!.CreateContext())
        {
            await _database!.MigrateEmptyToAsync(PreviousMigration(db));
            await LegacyOrgRows.InsertAsync(db, new OrgEntity
            {
                Id = id,
                Name = "Org E40",
                Slug = $"e40-{id:N}",
                DisplayName = "Org E40",
                ContactEmail = "e40@example.com",
                PlanTier = PlanTier.Pro,
                IsActive = true,
            });

            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var org = await after.Orgs.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == id);
        Assert.False(org.GuestAreaOnOwnDomain);
        Assert.False(org.OwnerPortalOnOwnDomain);
        Assert.True(org.UseOwnDomainForLinks);
        Assert.Equal(OwnerLoginHostState.NotRequested, org.OwnerLoginHostState);
        Assert.Null(org.GtmContainerId);
        Assert.Equal("Org E40", org.DisplayName);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddHostSurfaces", StringComparison.Ordinal));
        Assert.True(index > 0, "AddHostSurfaces migration not found.");
        return all[index - 1];
    }
}
