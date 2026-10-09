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
/// SP-05 and the admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14) on PostgreSQL. Since SP-05 the engagements of
/// a calendar feed are unique per supplier, UID and start: when both profiles of a duplicate pair synced the same calendar, moving
/// the duplicate's windows to the keeper would hit that index and fail the whole merge. The keeper's row stays, the duplicate's
/// copy goes with its profile, and every other window of the duplicate (a different engagement, a block, an extra opening) still
/// moves. A class of its own, like <see cref="SupplierAgendaRepairPostgresTests"/>: the repair spans every org of the database
/// and each test drops the unique email index to reproduce the data written before it.
/// </summary>
public class SupplierCalendarSyncHoursRepairPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateTime Older = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Ten = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task FixOrphaned_BothProfilesSyncedTheSameCalendar_TheMergeSucceeds_AndTheKeeperKeepsItsRows()
    {
        await DropEmailIndexAsync();
        var email = $"sp05-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        var keeperShared = await SeedWindowAsync(keeper, Feed("shared", Ten));
        await SeedWindowAsync(keeper, Feed("only-keeper", Ten.AddDays(1)));
        await SeedWindowAsync(duplicate, Feed("shared", Ten));                 // the same occurrence: stays with the keeper
        await SeedWindowAsync(duplicate, Feed("shared", Ten.AddDays(7)));      // the same event, another occurrence: moves
        await SeedWindowAsync(duplicate, Feed("only-duplicate", Ten.AddDays(2)));
        await SeedWindowAsync(duplicate, Manual(SupplierBusyWindowKind.Block, Ten));   // set by hand, at the hour of an engagement: moves
        await SeedWindowAsync(duplicate, Manual(SupplierBusyWindowKind.ExtraOpening, Ten.AddDays(3)));

        var dry = await FixOrphanedAsync(dryRun: true);
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.Equal(4, SingleMerge(await ReadAsync(dry), duplicate).GetProperty("agendaRowsMoved").GetInt32());
        Assert.Equal(5, await WindowCountAsync(duplicate)); // a dry run changes nothing

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(4, merge.GetProperty("agendaRowsMoved").GetInt32()); // the copy that stayed behind is not counted as moved
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        Assert.Equal(0, await WindowCountAsync(duplicate));
        var windows = await WindowsAsync(keeper);
        Assert.Equal(6, windows.Count);
        // One row per occurrence of the engagements, and the one the keeper had before is the same row.
        Assert.Equal(keeperShared.Id, windows.Single(w => w.ExternalUid == "shared" && w.StartUtc == Ten).Id);
        Assert.Equal(2, windows.Count(w => w.ExternalUid == "shared"));
        Assert.Contains(windows, w => w.ExternalUid == "only-keeper");
        Assert.Contains(windows, w => w.ExternalUid == "only-duplicate");
        Assert.Equal(2, windows.Count(w => w.ExternalUid is null));
    }

    [PostgresFact]
    public async Task FixOrphaned_ADuplicateWithEngagementsTheKeeperLacks_MovesThemAll()
    {
        await DropEmailIndexAsync();
        var email = $"sp05-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SeedWindowAsync(duplicate, Feed("a", Ten));
        await SeedWindowAsync(duplicate, Feed("b", Ten.AddDays(1)));

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(2, SingleMerge(await ReadAsync(applied), duplicate).GetProperty("agendaRowsMoved").GetInt32());
        Assert.Equal(["a", "b"], (await WindowsAsync(keeper)).Select(w => w.ExternalUid));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<HttpResponseMessage> FixOrphanedAsync(bool dryRun)
    {
        using var admin = factory.CreateAuthenticatedClient($"auth0|sp05-admin-{Guid.NewGuid():N}", roles: "Admin");
        return await admin.PostAsync($"/api/admin/suppliers/fix-orphaned?dryRun={(dryRun ? "true" : "false")}", null);
    }

    /// <summary>Data written before the migration SupplierProfileEmailUnique: without its unique index.</summary>
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
            Name = "Fornitore SP-05",
            Slug = $"sp05-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SP-05",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-05 Srl",
            Phone = "+39 06 050505",
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

    private async Task<SupplierBusyWindow> SeedWindowAsync(Guid orgId, SupplierBusyWindow window)
    {
        window.OrgId = orgId;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierBusyWindows.Add(window);
        await db.SaveChangesAsync();
        return window;
    }

    private async Task<int> WindowCountAsync(Guid orgId) => (await SupplierAgendaTestData.CountsAsync(factory, orgId)).Windows;

    private async Task<List<SupplierBusyWindow>> WindowsAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.SupplierBusyWindows.AsNoTracking().Where(w => w.OrgId == orgId).OrderBy(w => w.StartUtc).ThenBy(w => w.ExternalUid).ToListAsync();
    }

    private static SupplierBusyWindow Feed(string uid, DateTime start) =>
        new()
        {
            StartUtc = start,
            EndUtc = start.AddHours(1),
            Kind = SupplierBusyWindowKind.External,
            Source = SupplierBusyWindowSource.ICalFeed,
            ExternalUid = uid,
        };

    private static SupplierBusyWindow Manual(SupplierBusyWindowKind kind, DateTime start) =>
        new() { StartUtc = start, EndUtc = start.AddHours(1), Kind = kind, Source = SupplierBusyWindowSource.Manual };

    private static JsonElement SingleMerge(JsonElement report, Guid duplicateOrgId) =>
        Assert.Single(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == duplicateOrgId);
}
