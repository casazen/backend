using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-07 (A5-21): on a real PostgreSQL database, the <c>AddDl145SafetyChecklist</c> migration moves the old JSON
/// checklists (<c>Properties.SafetyChecklistJson</c>) to the new tables without losing anything: the old answers go to
/// the right item or stay "to review", the old text is kept verbatim, and the host confirms again. Down puts the JSON
/// back.
/// </summary>
public class AddDl145SafetyChecklistMigrationPostgresTests : IAsyncLifetime
{
    private const string MigrationSuffix = "_AddDl145SafetyChecklist";

    private const string FullLegacyJson =
        """{"smokeDetector":true,"fireExtinguisher":true,"gasCompliance":true,"acknowledgedAt":"2026-01-01T00:00:00Z","acknowledgedBy":"auth0|old"}""";

    private const string PartialLegacyJson = """{"smokeDetector":false,"fireExtinguisher":true,"gasCompliance":false}""";

    private const string MalformedLegacyJson = "not json at all";

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task AddDl145SafetyChecklist_OldAnswers_BecomeTheRightItemOrToReviewWithoutLoss()
    {
        await using var db = _database!.CreateContext();
        var migrator = db.GetService<IMigrator>();
        migrator.Migrate(Migration(db, offset: -1));
        var seed = await SeedPreviousStateAsync(db);

        migrator.Migrate(Migration(db, offset: 0));

        db.ChangeTracker.Clear();
        var checklists = await db.PropertySafetyChecklists
            .AsNoTracking()
            .Include(c => c.Items)
            .ToDictionaryAsync(c => c.PropertyId);
        Assert.Equal(3, checklists.Count);
        Assert.False(checklists.ContainsKey(seed.WithoutChecklist));

        var full = checklists[seed.Full];
        Assert.Equal(SafetyChecklistRules.LegacySchemaVersion, full.SchemaVersion);
        Assert.Equal(FullLegacyJson, full.LegacyChecklistJson);
        Assert.Equal(seed.Org, full.OrgId);
        Assert.Null(full.ConfirmedAt); // the old acknowledgment does not confirm the new checklist
        Assert.Null(full.Entrepreneurial);
        Assert.Null(full.HasGasSupply);
        Assert.Equal(
            new Dictionary<SafetyItemCode, SafetyItemAnswer?>
            {
                [SafetyItemCode.FireExtinguishers] = SafetyItemAnswer.ToReview,
                [SafetyItemCode.GasDetector] = null,
                [SafetyItemCode.CoDetector] = null,
                [SafetyItemCode.SystemsCompliance] = SafetyItemAnswer.ToReview, // gas certificate = gas system conformity
                [SafetyItemCode.BdsrDeclaration] = null,
                [SafetyItemCode.SmokeDetector] = SafetyItemAnswer.Present,
                [SafetyItemCode.EmergencyInstructions] = null,
            },
            full.Items.ToDictionary(i => i.Code, i => i.Answer));
        Assert.All(full.Items, i => Assert.Equal(seed.Org, i.OrgId));

        var partial = checklists[seed.Partial].Items.ToDictionary(i => i.Code, i => i.Answer);
        Assert.Equal(SafetyItemAnswer.ToReview, partial[SafetyItemCode.FireExtinguishers]);
        Assert.Null(partial[SafetyItemCode.SystemsCompliance]); // "no" could also mean "not answered"
        Assert.Null(partial[SafetyItemCode.SmokeDetector]);

        var malformed = checklists[seed.Malformed];
        Assert.Equal(MalformedLegacyJson, malformed.LegacyChecklistJson);
        Assert.Equal(SafetyChecklistRules.Items.Count, malformed.Items.Count);
        Assert.All(malformed.Items, i => Assert.Null(i.Answer));

        // The imported checklist blocks until the host reviews and confirms it; the smoke detector never blocks.
        var blockers = SafetyChecklistRules.Evaluate(full).Blockers.Select(b => b.Code).ToList();
        Assert.Contains("safety_extinguishers_review", blockers);
        Assert.Contains("safety_confirmation_missing", blockers);
        Assert.DoesNotContain(blockers, code => code.Contains("smoke", StringComparison.Ordinal));

        // Down: the old JSON is back on the property.
        migrator.Migrate(Migration(db, offset: -1));
        Assert.Equal(FullLegacyJson, await LegacyJsonAsync(db, seed.Full));
        Assert.Equal(PartialLegacyJson, await LegacyJsonAsync(db, seed.Partial));
        Assert.Equal(MalformedLegacyJson, await LegacyJsonAsync(db, seed.Malformed));
        Assert.Null(await LegacyJsonAsync(db, seed.WithoutChecklist));
    }

    private sealed record Seed(Guid Org, Guid Full, Guid Partial, Guid Malformed, Guid WithoutChecklist);

    private static string Migration(AppDbContext db, int offset)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith(MigrationSuffix, StringComparison.Ordinal));
        Assert.True(index > 0);
        return all[index + offset];
    }

    /// <summary>
    /// Orgs are written through the model; Properties and the old JSON column with SQL (later migrations add columns to
    /// the current Property entity).
    /// </summary>
    private static async Task<Seed> SeedPreviousStateAsync(AppDbContext db)
    {
        var org = new OrgEntity
        {
            Name = "Org CO-07",
            Slug = $"co07-{Guid.NewGuid():N}",
            DisplayName = "Org CO-07",
            ContactEmail = "co07@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);

        await db.SaveChangesAsync();

        // Property rows in SQL: the current Property entity has columns that later migrations add (LT-10).
        async Task<Guid> NewPropertyAsync(string name)
        {
            var id = Guid.NewGuid();
            var address = $"Via Roma {Guid.NewGuid():N}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Properties" (
                    "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                    "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                    "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "ComplianceStatus", "CreatedAt", "UpdatedAt")
                VALUES ({id}, 'auth0|co07', {org.Id}, {name}, 'Casa', {address}, 'Roma', '00100', 0, 0, 0, 0, 4, 100, 0, 0,
                    ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, {(int)PropertyComplianceStatus.Active}, now(), now());
                """);
            return id;
        }

        var full = await NewPropertyAsync("Full");
        var partial = await NewPropertyAsync("Partial");
        var malformed = await NewPropertyAsync("Malformed");
        var without = await NewPropertyAsync("Without");

        await SetLegacyJsonAsync(db, full, FullLegacyJson);
        await SetLegacyJsonAsync(db, partial, PartialLegacyJson);
        await SetLegacyJsonAsync(db, malformed, MalformedLegacyJson);
        return new Seed(org.Id, full, partial, malformed, without);
    }

    private static Task SetLegacyJsonAsync(AppDbContext db, Guid propertyId, string json) =>
        db.Database.ExecuteSqlAsync($"""UPDATE "Properties" SET "SafetyChecklistJson" = {json} WHERE "Id" = {propertyId}""");

    private static async Task<string?> LegacyJsonAsync(AppDbContext db, Guid propertyId) =>
        await db.Database
            .SqlQuery<string?>($"""SELECT "SafetyChecklistJson" AS "Value" FROM "Properties" WHERE "Id" = {propertyId}""")
            .SingleAsync();
}
