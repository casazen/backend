using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-03 and the admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14) on PostgreSQL: the agenda of a duplicate
/// supplier profile moves to the keeper before the profile is deleted (the foreign keys cascade, so what stayed would be
/// deleted with it): the time off, blocks and extra openings always, the weekly hours and the settings only when the keeper
/// has none (the keeper's own are the ones in use), and a dry run reports the move and changes nothing. A class of its own, as
/// <see cref="SupplierServiceCatalogRepairPostgresTests"/>: the repair spans every org of the database and each test drops the
/// unique email index to reproduce the data written before it.
/// </summary>
public class SupplierAgendaRepairPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateTime Older = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Configured = new(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithAnAgenda_MovesItAllToAKeeperThatHasNone()
    {
        await DropEmailIndexAsync();
        var email = $"sp03-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedAgendaAsync(duplicate, buffer: 45, maxJobs: 6);

        var dry = await FixOrphanedAsync(dryRun: true);
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.Equal(3 + 2 + 2 + 1, SingleMerge(await ReadAsync(dry), duplicate).GetProperty("agendaRowsMoved").GetInt32());
        Assert.Equal((3, 2, 2, 1), await CountsAsync(duplicate));
        Assert.Equal((0, 0, 0, 0), await CountsAsync(keeper));

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(8, merge.GetProperty("agendaRowsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        Assert.False(await ProfileExistsAsync(duplicate));
        Assert.Equal((0, 0, 0, 0), await CountsAsync(duplicate));
        Assert.Equal((3, 2, 2, 1), await CountsAsync(keeper));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.SupplierSettings.AsNoTracking().Where(s => s.OrgId == keeper).SingleAsync();
        Assert.Equal(45, settings.BufferMinutes);
        Assert.Equal(6, settings.MaxJobsPerDay);
        Assert.Equal(Configured, settings.HoursConfiguredAt);
    }

    [PostgresFact]
    public async Task FixOrphaned_AKeeperWithItsOwnHoursAndSettings_KeepsThem_AndGetsTheClosuresOfTheDuplicate()
    {
        await DropEmailIndexAsync();
        var email = $"sp03-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedAgendaAsync(keeper, buffer: 15, maxJobs: 2, hourBands: 1, timeOff: 1, windows: 0);
        await SeedAgendaAsync(duplicate, buffer: 45, maxJobs: 6);

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(4, SingleMerge(await ReadAsync(applied), duplicate).GetProperty("agendaRowsMoved").GetInt32());
        Assert.Equal((1, 3, 2, 1), await CountsAsync(keeper));
        Assert.Equal((0, 0, 0, 0), await CountsAsync(duplicate));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.SupplierSettings.AsNoTracking().Where(s => s.OrgId == keeper).SingleAsync();
        Assert.Equal(15, settings.BufferMinutes);
        Assert.Equal(2, settings.MaxJobsPerDay);
        Assert.Single(await db.SupplierWorkingHours.AsNoTracking().Where(h => h.OrgId == keeper).ToListAsync());
    }

    [PostgresFact]
    public async Task FixOrphaned_AKeeperWithRulesButNoHours_GetsTheHoursOfTheDuplicate_AndTheyCountAsConfigured()
    {
        await DropEmailIndexAsync();
        var email = $"sp03-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedAgendaAsync(keeper, buffer: 15, maxJobs: 2, hourBands: 0, timeOff: 0, windows: 0, hoursConfiguredAt: null);
        await SeedAgendaAsync(duplicate, buffer: 45, maxJobs: 6);

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal((3, 2, 2, 1), await CountsAsync(keeper));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.SupplierSettings.AsNoTracking().Where(s => s.OrgId == keeper).SingleAsync();
        Assert.Equal(15, settings.BufferMinutes);
        Assert.Equal(Configured, settings.HoursConfiguredAt);
    }

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithoutAnAgenda_MergesAsBeforeAndReportsNone()
    {
        await DropEmailIndexAsync();
        var email = $"sp03-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedAgendaAsync(keeper, buffer: 15, maxJobs: 2);

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(0, merge.GetProperty("agendaRowsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());
        Assert.Equal((3, 2, 2, 1), await CountsAsync(keeper));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<(int Hours, int TimeOff, int Windows, int Settings)> CountsAsync(Guid orgId) =>
        await SupplierAgendaTestData.CountsAsync(factory, orgId);

    private async Task<bool> ProfileExistsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().SupplierProfiles.AnyAsync(sp => sp.OrgId == orgId);
    }

    private async Task<HttpResponseMessage> FixOrphanedAsync(bool dryRun)
    {
        using var admin = factory.CreateAuthenticatedClient($"auth0|sp03-admin-{Guid.NewGuid():N}", roles: "Admin");
        return await admin.PostAsync($"/api/admin/suppliers/fix-orphaned?dryRun={(dryRun ? "true" : "false")}", null);
    }

    private async Task DropEmailIndexAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync($"DROP INDEX IF EXISTS \"{SupplierProfileEmailIndex.Name}\"");
    }

    private async Task<Guid> SeedProfileAsync(string email, SupplierStatus status, DateTime createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore SP-03",
            Slug = $"sp03-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SP-03",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-03 Srl",
            Phone = "+39 06 030303",
            Status = status,
            CategoriesJson = "[]",
            ComuniJson = """["H501"]""",
            TosAcceptedAt = status == SupplierStatus.Active ? createdAt : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task SeedAgendaAsync(
        Guid orgId,
        int buffer,
        int maxJobs,
        int hourBands = 3,
        int timeOff = 2,
        int windows = 2,
        DateTime? hoursConfiguredAt = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bands = new[]
        {
            (DayOfWeek.Monday, 480, 780),
            (DayOfWeek.Monday, 840, 1080),
            (DayOfWeek.Tuesday, 480, 780),
        };
        foreach (var (weekday, start, end) in bands.Take(hourBands))
            db.SupplierWorkingHours.Add(new SupplierWorkingHours { OrgId = orgId, Weekday = weekday, StartMinute = start, EndMinute = end });

        for (var i = 0; i < timeOff; i++)
            db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = new DateOnly(2026, 12, 20 + (i * 5)), ToDate = new DateOnly(2026, 12, 22 + (i * 5)) });

        for (var i = 0; i < windows; i++)
        {
            var start = new DateTime(2026, 11, 10 + i, 8, 0, 0, DateTimeKind.Utc);
            db.SupplierBusyWindows.Add(new SupplierBusyWindow
            {
                OrgId = orgId,
                StartUtc = start,
                EndUtc = start.AddHours(1),
                Kind = i == 0 ? SupplierBusyWindowKind.Block : SupplierBusyWindowKind.ExtraOpening,
            });
        }

        db.SupplierSettings.Add(new SupplierSettings
        {
            OrgId = orgId,
            BufferMinutes = buffer,
            MaxJobsPerDay = maxJobs,
            HoursConfiguredAt = hourBands > 0 ? hoursConfiguredAt ?? Configured : hoursConfiguredAt,
        });
        await db.SaveChangesAsync();
    }

    private static JsonElement SingleMerge(JsonElement report, Guid duplicateOrgId) =>
        Assert.Single(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == duplicateOrgId);
}
