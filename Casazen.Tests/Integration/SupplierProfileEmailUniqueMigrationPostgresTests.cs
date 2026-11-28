using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-14 migration (<see cref="SupplierProfileEmailUnique"/>) on real PostgreSQL, on the schema as it is now (a copy of the migrated
/// template): the unique index on <c>lower(btrim("Email"))</c> refuses an email that differs only in case and spaces and lets the
/// blank emails in. How the migration merged the duplicates written before it (with the rules of fix-orphaned) and how it reported
/// the profiles held by different accounts ran when the release that carried it went to production: it is not replayed any more.
/// </summary>
public class SupplierProfileEmailUniqueMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateMigratedAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task UniqueIndex_EmailDifferingOnlyInCaseAndSpaces_IsRejectedButBlankEmailsAreAllowed()
    {
        await using (var db = _database!.CreateContext())
        {
            AddProfile(db, "Case@Example.com");
            AddProfile(db, string.Empty);
            AddProfile(db, "   ");
            await db.SaveChangesAsync();
        }

        await using var duplicate = _database.CreateContext();
        AddProfile(duplicate, "  case@EXAMPLE.com ");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());

        Assert.True(SupplierProfileEmailIndex.IsViolation(error));
        Assert.Equal(SupplierProfileEmailUnique.IndexName, SupplierProfileEmailIndex.Name);
    }

    private static void AddProfile(AppDbContext db, string email)
    {
        var org = new OrgEntity
        {
            Name = "Fornitore",
            Slug = $"su14-idx-{Guid.NewGuid():N}",
            DisplayName = "Fornitore",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile { OrgId = org.Id, Email = email, LegalName = "Fornitore", Phone = "" });
    }
}
