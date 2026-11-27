using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PM-01 (decision D19) on real PostgreSQL, on the schema as it is now (a copy of the migrated template): <c>Properties.RentalMode</c>
/// has its index with the org, and its column default is <c>Short</c>, so a writer that does not know the column still creates
/// short-stay properties. Which live properties the <c>AddPropertyRentalMode</c> migration turned to <c>Long</c> (and its dry run, its
/// rerun and its revert) ran when the release that carried it went to production: it is not replayed any more.
/// </summary>
public class AddPropertyRentalModeMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime UpdatedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task NewColumnAndIndex_AreThere_AndAnInsertWithoutTheModeIsShort()
    {
        var org = Guid.NewGuid();
        await using var db = _database!.CreateContext();
        await LegacyOrgRows.InsertAsync(db, new OrgEntity
        {
            Id = org,
            Name = "Org PM-01",
            Slug = $"pm01-{org:N}",
            DisplayName = "Org PM-01",
            ContactEmail = "pm01@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });

        var indexes = await db.Database
            .SqlQuery<string>($"""SELECT indexname::text AS "Value" FROM pg_indexes WHERE tablename = 'Properties'""")
            .ToListAsync();
        Assert.Contains("IX_Properties_OrgId_RentalMode", indexes);

        var late = Guid.NewGuid();
        await InsertPropertyAsync(db, late, org, "Scritta dal vecchio codice", maxGuests: 0, nightlyRate: 0);
        Assert.Equal(RentalMode.Short, await ModeAsync(late));
    }

    private static Task InsertPropertyAsync(AppDbContext db, Guid id, Guid orgId, string name, int maxGuests, decimal nightlyRate) =>
        LegacyPropertyRows.InsertAsync(db, new Property
        {
            Id = id,
            OrgId = orgId,
            OwnerId = "auth0|pm01",
            Name = name,
            Description = "PM-01",
            Address = "Via PM-01 " + id.ToString("N"),
            City = "Monza",
            PostalCode = "20900",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = maxGuests,
            NightlyRate = nightlyRate,
            UpdatedAt = UpdatedAt,
        });

    private async Task<RentalMode> ModeAsync(Guid propertyId)
    {
        await using var db = _database!.CreateContext();
        return await db.Properties.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Id == propertyId)
            .Select(p => p.RentalMode)
            .SingleAsync();
    }
}
