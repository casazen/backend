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
/// SP-02 and the admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14) on PostgreSQL: the services of a duplicate
/// supplier profile move to the keeper before the profile is deleted (the foreign key cascades, so what stayed would be
/// deleted with it), a slug the keeper already uses gets the next free suffix, the deleted services move too, and a dry run
/// reports the move and changes nothing. A class of its own: the repair spans every org of the database and each test drops
/// the unique email index to reproduce the data written before it (<see cref="SupplierRepairPostgresTests"/> does the same).
/// </summary>
public class SupplierServiceCatalogRepairPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateTime Older = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithServices_MovesThemAllToTheKeeperRenamingCollidingSlugs()
    {
        await DropEmailIndexAsync();
        var email = $"sp02-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        var keeperOwn = SupplierCatalogTestData.Row(keeper, "pulizia");
        var keeperDeleted = SupplierCatalogTestData.Row(keeper, "vecchio", deletedAt: Newer);
        var collides = SupplierCatalogTestData.Row(duplicate, "pulizia");
        var free = SupplierCatalogTestData.Row(duplicate, "sanificazione");
        var deleted = SupplierCatalogTestData.Row(duplicate, "vecchio", deletedAt: Newer);
        foreach (var row in new[] { keeperOwn, keeperDeleted, collides, free, deleted })
            await SupplierCatalogTestData.SaveRowAsync(factory, row);

        // The dry run reports the move and changes nothing.
        var dry = await FixOrphanedAsync(dryRun: true);
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        Assert.Equal(3, SingleMerge(await ReadAsync(dry), duplicate).GetProperty("serviceListingsMoved").GetInt32());
        Assert.Equal(3, await CountAsync(duplicate));

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(3, merge.GetProperty("serviceListingsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Nothing is lost with the duplicate profile (the cascade would have taken what stayed): every row is the keeper's.
        Assert.False(await db.SupplierProfiles.AnyAsync(sp => sp.OrgId == duplicate));
        Assert.Equal(0, await db.SupplierServiceListings.CountAsync(l => l.OrgId == duplicate));
        var moved = await db.SupplierServiceListings.AsNoTracking().Where(l => l.OrgId == keeper).ToListAsync();
        Assert.Equal(5, moved.Count);

        // The slug the keeper already used got the next free suffix; the free one and the deleted ones kept theirs.
        Assert.Equal("pulizia", moved.Single(l => l.Id == keeperOwn.Id).Slug);
        Assert.Equal("pulizia-2", moved.Single(l => l.Id == collides.Id).Slug);
        Assert.Equal("sanificazione", moved.Single(l => l.Id == free.Id).Slug);
        Assert.Equal("vecchio", moved.Single(l => l.Id == deleted.Id).Slug);
        Assert.NotNull(moved.Single(l => l.Id == deleted.Id).DeletedAt);
        // ...so the unique index still holds for the services that are not deleted.
        var live = moved.Where(l => l.DeletedAt is null).Select(l => l.Slug).ToList();
        Assert.Equal(live.Count, live.Distinct().Count());
    }

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithoutServices_MergesAsBeforeAndReportsNone()
    {
        await DropEmailIndexAsync();
        var email = $"sp02-{Guid.NewGuid():N}@test.com";
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        await SupplierCatalogTestData.SaveRowAsync(factory, SupplierCatalogTestData.Row(keeper, "pulizia"));

        var applied = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var merge = SingleMerge(await ReadAsync(applied), duplicate);
        Assert.Equal(0, merge.GetProperty("serviceListingsMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());
        // The keeper's own catalog is untouched.
        Assert.Equal(1, await CountAsync(keeper));
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<int> CountAsync(Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierServiceListings.CountAsync(l => l.OrgId == orgId);
    }

    private async Task<HttpResponseMessage> FixOrphanedAsync(bool dryRun)
    {
        using var admin = factory.CreateAuthenticatedClient($"auth0|sp02-admin-{Guid.NewGuid():N}", roles: "Admin");
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
            Name = "Fornitore SP-02",
            Slug = $"sp02-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SP-02",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SP-02 Srl",
            Phone = "+39 06 020202",
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

    private static JsonElement SingleMerge(JsonElement report, Guid duplicateOrgId) =>
        Assert.Single(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == duplicateOrgId);
}
