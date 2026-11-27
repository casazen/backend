using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Configurations;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// The rules the database holds for the ISTAT region table (E21/F01/US01/T01), on a real PostgreSQL database, where the constraints live: a row with no https
/// source, a day on a deadline that is not monthly, a monthly deadline with no day, a field outside the three lists, a malformed code or a value outside the
/// enums is refused, and so is a second row for one territory; what is valid is stored. The unit tests run on EF InMemory, which ignores checks and indexes,
/// so only here are the rules of the table proven. The migration <c>FixIstatDeadlineCheck</c> is tried on a table that holds the row the old check let in.
/// </summary>
public class IstatRegionRulesPostgresTests
{
    private const string Source = "https://www.example.gov.it/fonte";

    private static readonly DateTime Moment = new(2026, 11, 19, 9, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task IstatRegionRules_TheDatabaseRefusesWhatItMustRefuse_AndStoresWhatIsValid_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("istat");

        // The seed the migration wrote: 21 territories, none covered, none verified, and the four monthly deadlines with their day.
        await using (var db = database.CreateContext())
        {
            var seeded = await db.IstatRegionRules.AsNoTracking().ToListAsync();
            Assert.Equal(21, seeded.Count);
            Assert.All(seeded, row =>
            {
                Assert.Equal(IstatCoverage.Unknown, row.OctorateCoverage);
                Assert.Null(row.VerifiedAt);
            });
            Assert.Equal(
                ["02:5", "03:5", "10:5", "17:10"],
                seeded.Where(r => r.DeadlineKind == IstatDeadlineKind.Monthly).Select(r => $"{r.RegionIstatCode}:{r.DeadlineDay}").Order());
        }

        // What it must refuse, each on a territory the seed does not have so that only the check under test can fail.
        var refused = new (string What, Action<IstatRegionRule> Edit, string Constraint)[]
        {
            ("a source that is not https", r => r.SourceUrl = "http://www.example.gov.it/non-sicuro", IstatRegionRuleConfiguration.SourceCheckName),
            ("a source with an upper case scheme (the service stores it in lower case)", r => r.SourceUrl = "HTTPS://www.example.gov.it/fonte", IstatRegionRuleConfiguration.SourceCheckName),
            ("an empty source", r => r.SourceUrl = string.Empty, IstatRegionRuleConfiguration.SourceCheckName),
            ("a portal that is not https", r => r.PortalUrl = "ftp://portale.example.it", IstatRegionRuleConfiguration.PortalCheckName),
            ("a monthly deadline with no day", r => r.DeadlineKind = IstatDeadlineKind.Monthly, IstatRegionRuleConfiguration.DeadlineCheckName),
            (
                "a monthly deadline with day 0",
                r =>
                {
                    r.DeadlineKind = IstatDeadlineKind.Monthly;
                    r.DeadlineDay = 0;
                },
                IstatRegionRuleConfiguration.DeadlineCheckName),
            (
                "a monthly deadline with day 32",
                r =>
                {
                    r.DeadlineKind = IstatDeadlineKind.Monthly;
                    r.DeadlineDay = 32;
                },
                IstatRegionRuleConfiguration.DeadlineCheckName),
            (
                "a day on a daily deadline",
                r =>
                {
                    r.DeadlineKind = IstatDeadlineKind.Daily;
                    r.DeadlineDay = 3;
                },
                IstatRegionRuleConfiguration.DeadlineCheckName),
            ("a day on an unknown deadline", r => r.DeadlineDay = 5, IstatRegionRuleConfiguration.DeadlineCheckName),
            ("a field outside the three lists", r => r.RequiredGuestFields = "TourismType,Hobby", IstatRegionRuleConfiguration.RequiredFieldsCheckName),
            ("a field in lower case", r => r.RequiredGuestFields = "tourismtype", IstatRegionRuleConfiguration.RequiredFieldsCheckName),
            ("an empty item in the list", r => r.RequiredGuestFields = "TourismType,,TravelWay", IstatRegionRuleConfiguration.RequiredFieldsCheckName),
            ("a deadline kind that is not a value of the enum", r => r.DeadlineKind = (IstatDeadlineKind)7, IstatRegionRuleConfiguration.EnumsCheckName),
            ("a coverage that is not a value of the enum", r => r.OctorateCoverage = (IstatCoverage)3, IstatRegionRuleConfiguration.EnumsCheckName),
            ("a region code of one digit", r => r.RegionIstatCode = "9", IstatRegionRuleConfiguration.RegionCodeCheckName),
            ("a region code with a letter", r => r.RegionIstatCode = "A1", IstatRegionRuleConfiguration.RegionCodeCheckName),
            ("a province in lower case", r => r.ProvinceCode = "bz", IstatRegionRuleConfiguration.ProvinceCodeCheckName),
            ("a province of one letter", r => r.ProvinceCode = "B", IstatRegionRuleConfiguration.ProvinceCodeCheckName),
        };

        foreach (var (what, edit, constraint) in refused)
        {
            var row = ValidRow("99");
            edit(row);
            await AssertRefusedAsync(database, row, PostgresErrorCodes.CheckViolation, constraint, what);
        }

        // What is valid is stored: a monthly deadline with its day (the first and the last of the month), a daily and an unknown one with none, a portal, and
        // the required fields as none (not verified), as an empty list (verified: none required), as each field alone and as the three together.
        var allFields = IstatRegionResolver.FormatFields(IstatRegionResolver.FieldNames.ToList());
        var valid = new List<IstatRegionRule>();
        var next = 70;
        IstatRegionRule Add(Action<IstatRegionRule> edit)
        {
            var row = ValidRow((next++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            edit(row);
            valid.Add(row);
            return row;
        }

        Add(r =>
        {
            r.DeadlineKind = IstatDeadlineKind.Monthly;
            r.DeadlineDay = 1;
        });
        Add(r =>
        {
            r.DeadlineKind = IstatDeadlineKind.Monthly;
            r.DeadlineDay = 31;
        });
        Add(r => r.DeadlineKind = IstatDeadlineKind.Daily);
        Add(r => r.PortalUrl = "https://portale.example.it/guida");
        Add(r => r.RequiredGuestFields = null);
        Add(r => r.RequiredGuestFields = string.Empty);
        Add(r => r.RequiredGuestFields = allFields);
        foreach (var field in IstatGuestFieldLists.ByField.Keys)
            Add(r => r.RequiredGuestFields = field);

        await using (var db = database.CreateContext())
        {
            db.IstatRegionRules.AddRange(valid);
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            Assert.Equal(21 + valid.Count, await db.IstatRegionRules.CountAsync());
            var empty = await db.IstatRegionRules.AsNoTracking().SingleAsync(r => r.Id == valid[5].Id);
            Assert.Equal(string.Empty, empty.RequiredGuestFields);
        }
    }

    [PostgresFact]
    public async Task IstatRegionRules_OneRowForEachTerritory_TheRegionAndEachAutonomousProvince_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("istatuq");

        // A second row for a region with no province (Lombardia has one): the partial index of the regions refuses it.
        await AssertRefusedAsync(database, ValidRow("03"), PostgresErrorCodes.UniqueViolation, IstatRegionRule.RegionIndexName, "a second row for Lombardia");

        // A second row for an autonomous province (Bolzano has one): the partial index of the provinces refuses it.
        await AssertRefusedAsync(database, ValidRow("04", "BZ"), PostgresErrorCodes.UniqueViolation, IstatRegionRule.RegionProvinceIndexName, "a second row for Bolzano");

        // The two indexes only hold for their own rows: a province of a region that has a row of its own is another territory and is stored.
        await using (var db = database.CreateContext())
        {
            db.IstatRegionRules.Add(ValidRow("03", "CO"));
            await db.SaveChangesAsync();
        }

        await using (var db = database.CreateContext())
        {
            Assert.Equal(22, await db.IstatRegionRules.CountAsync());
        }
    }

    /// <summary>
    /// The service never lets the database fail: an https address written with an upper case scheme (the check of the table is case-sensitive) is stored in
    /// lower case, with the audit line of the save, and what the rules of the service refuse (a monthly deadline with no day) is a refusal of the service.
    /// </summary>
    [PostgresFact]
    public async Task IstatRegionService_OnPostgres_StoresAnUpperCaseHttpsSchemeInLowerCase_AndNeverFailsInTheDatabase_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("istatsvc");
        await using var db = database.CreateContext();
        var service = new IstatRegionService(
            db,
            new Mock<IPropertySenderStore>().Object,
            Microsoft.Extensions.Options.Options.Create(new ComplianceOptions()),
            NullLogger<IstatRegionService>.Instance);

        var saved = await service.UpdateAsync(
            new IstatRegionRuleUpdate(
                "03",
                null,
                "ROSS1000",
                "Https://portale.example.it/Guida",
                IstatDeadlineKind.Monthly,
                5,
                ["TravelWay", "tourismtype"],
                IstatCoverage.NotCovered,
                "HTTPS://www.example.gov.it/Fonte",
                null,
                null),
            "auth0|staff-istat",
            CancellationToken.None);

        await using (var read = database.CreateContext())
        {
            var stored = await read.IstatRegionRules.AsNoTracking().SingleAsync(r => r.Id == saved.Id);
            Assert.Equal(
                ("https://www.example.gov.it/Fonte", "https://portale.example.it/Guida", "TourismType,TravelWay"),
                (stored.SourceUrl, stored.PortalUrl, stored.RequiredGuestFields));
            Assert.Equal((IstatDeadlineKind.Monthly, 5, IstatCoverage.NotCovered), (stored.DeadlineKind, stored.DeadlineDay, stored.OctorateCoverage));
            Assert.Equal(1, await read.RegulatoryDataAuditEntries.CountAsync(e => e.EntityId == saved.Id));
        }

        // The rules of the service come before the database: a monthly deadline with no day is a 422 and writes nothing.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => service.UpdateAsync(
            new IstatRegionRuleUpdate("03", null, null, null, IstatDeadlineKind.Monthly, null, null, IstatCoverage.Unknown, Source, null, null),
            "auth0|staff-istat",
            CancellationToken.None));
        Assert.Equal(IstatRegionErrors.DeadlineInvalidCode, refused.Code);
        await using var after = database.CreateContext();
        Assert.Equal(1, await after.RegulatoryDataAuditEntries.CountAsync(e => e.EntityId == saved.Id));
    }

    /// <summary>
    /// The migration <c>FixIstatDeadlineCheck</c> on a table that holds what the check of the first migration let in (a monthly deadline with no day): the row
    /// becomes «not stated» (a deadline is never guessed) instead of stopping the migration, the four seeded monthly deadlines keep their day, and the next
    /// monthly row with no day is refused by the new check.
    /// </summary>
    [PostgresFact]
    public async Task FixIstatDeadlineCheck_AMonthlyRowWithNoDay_BecomesNotStated_AndTheNextOneIsRefused_Test()
    {
        await using var database = await PostgresTestDatabase.CreateAsync("istatfix");
        var id = Guid.NewGuid();
        await using (var db = database.CreateContext())
        {
            await database.MigrateEmptyToAsync(PreviousMigration(db));

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "IstatRegionRules" ("Id", "RegionIstatCode", "ProvinceCode", "RegionName", "DeadlineKind", "DeadlineDay", "OctorateCoverage", "SourceUrl", "UpdatedAt")
                VALUES ({id}, '98', NULL, 'Territorio di prova', 2, NULL, 0, 'https://www.example.gov.it/fonte', {Moment});
                """);
            await db.Database.MigrateAsync();
        }

        await using (var after = database.CreateContext())
        {
            var repaired = await after.IstatRegionRules.AsNoTracking().SingleAsync(r => r.Id == id);
            Assert.Equal(IstatDeadlineKind.Unknown, repaired.DeadlineKind);
            Assert.Null(repaired.DeadlineDay);
            Assert.Equal(4, await after.IstatRegionRules.CountAsync(r => r.DeadlineKind == IstatDeadlineKind.Monthly && r.DeadlineDay != null));
            Assert.Empty(await after.Database.GetPendingMigrationsAsync());
        }

        var monthlyWithNoDay = ValidRow("97");
        monthlyWithNoDay.DeadlineKind = IstatDeadlineKind.Monthly;
        await AssertRefusedAsync(database, monthlyWithNoDay, PostgresErrorCodes.CheckViolation, IstatRegionRuleConfiguration.DeadlineCheckName, "a monthly deadline with no day");
    }

    private static IstatRegionRule ValidRow(string region, string? province = null) => new()
    {
        RegionIstatCode = region,
        ProvinceCode = province,
        RegionName = "Territorio di prova",
        SourceUrl = Source,
        UpdatedAt = Moment,
    };

    /// <summary>Saves <paramref name="row"/> on a context of its own and says the database refused it, with this state and this constraint.</summary>
    private static async Task AssertRefusedAsync(PostgresTestDatabase database, IstatRegionRule row, string sqlState, string constraint, string what)
    {
        await using var db = database.CreateContext();
        db.IstatRegionRules.Add(row);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var postgres = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.True(
            (sqlState, constraint) == (postgres.SqlState, postgres.ConstraintName),
            $"{what}: expected {sqlState} on {constraint}, the database said {postgres.SqlState} on {postgres.ConstraintName}.");
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_FixIstatDeadlineCheck", StringComparison.Ordinal));
        Assert.True(index > 0, "FixIstatDeadlineCheck migration not found.");
        return all[index - 1];
    }
}
