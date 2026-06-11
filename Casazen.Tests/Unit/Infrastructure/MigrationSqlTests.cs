using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AC3/AC4/AC5/AC10b — migration correctness. Generates the REAL PostgreSQL migration script
/// from the Npgsql provider (no database connection required) and asserts the shipped SQL has
/// the safety properties the design mandates: nullable add → idempotent relationship-walk
/// backfill → pre-flight NULL guard before the NOT-NULL flip + restricted FKs, with a tested
/// down-migration. This is the Docker-free equivalent of running the migration end-to-end.
/// </summary>
public class MigrationSqlTests
{
    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=casazen_design;Username=postgres;Password=postgres",
                npgsql => npgsql.MigrationsAssembly("Casazen.Infrastructure"))
            .Options);

    private static (string addNullable, string backfill, string makeRequired) MigrationIds(AppDbContext db)
    {
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();
        return (
            keys.Single(k => k.EndsWith("AddOrgIdNullable", StringComparison.Ordinal)),
            keys.Single(k => k.EndsWith("BackfillDefaultOrgs", StringComparison.Ordinal)),
            keys.Single(k => k.EndsWith("MakeOrgIdRequired", StringComparison.Ordinal)));
    }

    [Fact]
    public void Migrations_LandInOrder_AsTheLastThree()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        var lastThree = keys.TakeLast(3).ToList();
        Assert.EndsWith("AddConnectStatusFields", lastThree[0]);
        Assert.EndsWith("AddAlloggiatiCheckInMvp", lastThree[1]);
        Assert.EndsWith("AddComplianceSeoEntities", lastThree[2]);
    }

    [Fact]
    public void AddAlloggiatiCheckInMvp_ExistsAfterConnectFields()
    {
        using var db = NewNpgsqlContext();
        var keys = db.GetService<IMigrationsAssembly>().Migrations.Keys.ToList();

        Assert.Contains(keys, k => k.EndsWith("AddAlloggiatiCheckInMvp", StringComparison.Ordinal));
        var connectIdx = keys.ToList().FindIndex(k => k.EndsWith("AddConnectStatusFields", StringComparison.Ordinal));
        var alloggiatiIdx = keys.ToList().FindIndex(k => k.EndsWith("AddAlloggiatiCheckInMvp", StringComparison.Ordinal));
        Assert.True(alloggiatiIdx > connectIdx);
    }

    [Fact]
    public void Step1_AddOrgIdNullable_CreatesOrgTableAndUniqueSlugIndex() // AC3
    {
        using var db = NewNpgsqlContext();
        var (_, backfill, _) = MigrationIds(db);
        var migrator = db.GetService<IMigrator>();

        // Up to (but excluding) the backfill == just Step 1.
        var script = migrator.GenerateScript(toMigration: backfill);

        Assert.Contains("CREATE TABLE \"Orgs\"", script);
        Assert.Contains("IX_Orgs_Slug", script);
        Assert.Contains("ADD \"OrgId\" uuid", script); // nullable add (no NOT NULL)
        Assert.Contains("\"StripeConnectedAccountId\"", script);
        Assert.DoesNotContain("SET NOT NULL", script); // Step 1 must not flip nullability
    }

    [Fact]
    public void Step2_BackfillDefaultOrgs_IsIdempotentRelationshipWalkWithFallbackAndLog() // AC4
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var fullScript = migrator.GenerateScript();

        Assert.Contains("ON CONFLICT (\"Slug\") DO NOTHING", fullScript); // idempotent org insert
        Assert.Contains("casazen-unassigned", fullScript);                // dedicated fallback Org
        Assert.Contains("RAISE NOTICE", fullScript);                      // verified row-count log
        // Relationship walk: payments derive their org from their booking.
        Assert.Contains("UPDATE \"Payments\"", fullScript);
        Assert.Contains("UPDATE \"Bookings\"", fullScript);
        Assert.Contains("UPDATE \"LeaseContracts\"", fullScript);
    }

    [Fact]
    public void Step3_MakeOrgIdRequired_GuardsThenFlipsNotNullAndAddsRestrictedFks() // AC5/AC10b
    {
        using var db = NewNpgsqlContext();
        var migrator = db.GetService<IMigrator>();
        var fullScript = migrator.GenerateScript();

        // Pre-flight NULL guard fires before any NOT-NULL flip (fail loud, never silent).
        Assert.Contains("RAISE EXCEPTION", fullScript);
        Assert.Contains("Pre-flight failed", fullScript);
        Assert.True(
            fullScript.IndexOf("Pre-flight failed", StringComparison.Ordinal)
            < fullScript.IndexOf("SET NOT NULL", StringComparison.Ordinal),
            "Pre-flight NULL guard must precede the NOT-NULL flip");

        Assert.Contains("SET NOT NULL", fullScript);
        foreach (var fk in new[]
        {
            "FK_Properties_Orgs_OrgId", "FK_Bookings_Orgs_OrgId",
            "FK_LeaseContracts_Orgs_OrgId", "FK_Payments_Orgs_OrgId", "FK_Users_Orgs_OrgId",
        })
        {
            Assert.Contains(fk, fullScript);
        }

        Assert.Contains("ON DELETE RESTRICT", fullScript);
    }

    [Fact]
    public void Step3_MakeOrgIdRequired_DownReverts_FksAndNullability() // AC10b (tested down)
    {
        using var db = NewNpgsqlContext();
        var (_, backfill, makeRequired) = MigrationIds(db);
        var migrator = db.GetService<IMigrator>();

        // Down of Step 3 only: from MakeOrgIdRequired back to BackfillDefaultOrgs.
        var down = migrator.GenerateScript(fromMigration: makeRequired, toMigration: backfill);

        Assert.Contains("DROP CONSTRAINT \"FK_Properties_Orgs_OrgId\"", down);
        Assert.Contains("DROP NOT NULL", down);
    }
}
