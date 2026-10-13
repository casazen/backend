using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// A1-27 on a real PostgreSQL database: <see cref="AdminService.GetCinComplianceAsync"/> and
/// <see cref="AdminService.GetStatsAsync"/> filter, count and paginate in SQL (the CIN regex reuses
/// <c>CinFormat</c>'s own pattern constants — Npgsql must translate <c>Regex.IsMatch</c> to Postgres' <c>~</c>
/// operator for this to work at all), instead of loading every property into memory.
/// </summary>
public class AdminServiceCinCompliancePostgresTests
{
    [PostgresFact]
    public async Task GetCinComplianceAsync_MoreRowsThanOnePage_PaginatesInSqlWithStableTotal()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("admincin");
        var orgId = await SeedOrgAsync(database);

        // 25 valid CINs (real BDSR examples share a comune prefix here; only the random tail differs) — 2 pages of 20.
        await using (var seed = database.CreateContext())
        {
            for (var i = 0; i < 25; i++)
            {
                seed.Properties.Add(NewProperty(orgId, $"P{i:00}", $"IT058091C2{i:00000000}"[..18]));
            }
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var service = new AdminService(db, NullLogger<AdminService>.Instance);

        var (page1, total1) = await service.GetCinComplianceAsync(null, page: 1, pageSize: 20);
        var (page2, total2) = await service.GetCinComplianceAsync(null, page: 2, pageSize: 20);

        Assert.Equal(25, total1);
        Assert.Equal(25, total2);
        Assert.Equal(20, page1.Count());
        Assert.Equal(5, page2.Count());
        // No overlap between pages (SQL Skip/Take, not an in-memory slice of an already-paged list).
        Assert.Empty(page1.Select(i => i.PropertyId).Intersect(page2.Select(i => i.PropertyId)));
    }

    [PostgresFact]
    public async Task GetCinComplianceAsync_FilterByStatus_MatchesCinFormatRulesInSql()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("admincin");
        var orgId = await SeedOrgAsync(database);

        await using (var seed = database.CreateContext())
        {
            seed.Properties.AddRange(
                NewProperty(orgId, "Valid", "IT058091C27G5FFZDZ"), // real BDSR example (Roma)
                NewProperty(orgId, "Missing", null),
                NewProperty(orgId, "Garbage", "NOT-A-CIN"),
                // Old CasaZen-invented format, normalized: IT + 15 digits — must be "invalid", not "valid" (CO-01).
                NewProperty(orgId, "LegacyFormat", "IT123451234567890"));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var service = new AdminService(db, NullLogger<AdminService>.Instance);

        var (valid, validTotal) = await service.GetCinComplianceAsync("valid", 1, 20);
        var (missing, missingTotal) = await service.GetCinComplianceAsync("missing", 1, 20);
        var (invalid, invalidTotal) = await service.GetCinComplianceAsync("invalid", 1, 20);

        Assert.Equal(1, validTotal);
        Assert.Equal("Valid", valid.Single().PropertyName);
        Assert.Equal(1, missingTotal);
        Assert.Equal("Missing", missing.Single().PropertyName);
        Assert.Equal(2, invalidTotal);
        Assert.Equal(["Garbage", "LegacyFormat"], invalid.Select(i => i.PropertyName).OrderBy(n => n));
    }

    [PostgresFact]
    public async Task GetCinComplianceAsync_PageBelowOne_ClampsToFirstPageInsteadOfNegativeOffset()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("admincin");
        var orgId = await SeedOrgAsync(database);
        await using (var seed = database.CreateContext())
        {
            seed.Properties.Add(NewProperty(orgId, "P1", "IT058091C27G5FFZDZ"));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var service = new AdminService(db, NullLogger<AdminService>.Instance);

        // A negative OFFSET makes Postgres raise an error (A1-26); page 0 must not reach the database as -1.
        var (items, total) = await service.GetCinComplianceAsync(null, page: 0, pageSize: 20);

        Assert.Equal(1, total);
        Assert.Single(items);
    }

    [PostgresFact]
    public async Task GetStatsAsync_MixOfCinCodes_CountsValidMissingInvalidInSql()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("admincin");
        var orgId = await SeedOrgAsync(database);
        await using (var seed = database.CreateContext())
        {
            seed.Properties.AddRange(
                NewProperty(orgId, "Valid1", "IT058091C27G5FFZDZ"),
                NewProperty(orgId, "Valid2", "IT015146A12HOLV2MZ"),
                NewProperty(orgId, "Missing", null),
                NewProperty(orgId, "Invalid", "BAD"));
            await seed.SaveChangesAsync();
        }

        await using var db = database.CreateContext();
        var service = new AdminService(db, NullLogger<AdminService>.Instance);

        var stats = await service.GetStatsAsync();

        Assert.Equal(4, stats.CinTotal);
        Assert.Equal(2, stats.CinValid);
        Assert.Equal(1, stats.CinMissing);
        Assert.Equal(1, stats.CinInvalid);
    }

    private static async Task<Guid> SeedOrgAsync(PostgresTestDatabase database)
    {
        var orgId = Guid.NewGuid();
        await using var db = database.CreateContext();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Org",
            Slug = "org-" + orgId.ToString("N")[..8],
            DisplayName = "Org",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Starter,
        });
        await db.SaveChangesAsync();
        return orgId;
    }

    // Address+City+PostalCode+IsActive is unique (PC-06): every seeded property needs its own address.
    private static Property NewProperty(Guid orgId, string name, string? cinCode) => new()
    {
        Id = Guid.NewGuid(),
        OrgId = orgId,
        OwnerId = "auth0|owner",
        Name = name,
        Address = $"Via {name} 1",
        City = "Roma",
        CinCode = cinCode,
    };
}
