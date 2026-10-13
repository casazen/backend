using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-02 on PostgreSQL, where it counts: the supplier's service catalog is keyed by the supplier org and not tenant-filtered,
/// so isolation between two suppliers, the unique slug per supplier (partial index), the <c>xmin</c> concurrency token, the
/// advisory lock (the limit of 30 services, the slugs and the photo list under parallel requests), the table's checks and the
/// merge of duplicate supplier profiles are proved on the real database. They are skipped locally when no PostgreSQL is
/// available and always run on CI (<see cref="PostgresFactAttribute"/>).
/// </summary>
public class SupplierServiceCatalogPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private const string Url = "/api/supplier/services";

    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    private static readonly byte[] PngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // ─── Isolation between suppliers ─────────────────────────────────────────────

    [PostgresFact]
    public async Task TwoSuppliers_NeverSeeOrChangeEachOthersServices_AndEachRowBelongsToItsOwnOrg()
    {
        var (aUser, aOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (bUser, bOrg) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var a = SupplierClient(aUser);
        using var b = SupplierClient(bUser);

        var fromA = await ReadAsync(await a.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia A", 4500, 90)));
        var fromB = await ReadAsync(await b.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia B", 5500, 60)));
        var aId = fromA.GetProperty("id").GetGuid();
        var bId = fromB.GetProperty("id").GetGuid();
        await a.PostAsync($"{Url}/{aId}/publish", null);

        // Each lists only its own.
        Assert.Equal(new[] { aId }, Ids(await ReadAsync(await a.GetAsync(Url))));
        Assert.Equal(new[] { bId }, Ids(await ReadAsync(await b.GetAsync(Url))));

        // B cannot reach A's service on any endpoint: 404, never 403 (it does not exist for B), and nothing changes.
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"{Url}/{aId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"{Url}/{aId}", new { name = "Rubato", category = "cleaning", version = fromA.GetProperty("version").GetUInt32() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"{Url}/{aId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"{Url}/{aId}/pause", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"{Url}/{aId}/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"{Url}/{aId}/duplicate", null)).StatusCode);

        var stillA = await ReadAsync(await a.GetAsync($"{Url}/{aId}"));
        Assert.Equal("Pulizia A", stillA.GetProperty("name").GetString());
        Assert.Equal("Active", stillA.GetProperty("status").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(aOrg, (await db.SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == aId)).OrgId);
        Assert.Equal(bOrg, (await db.SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == bId)).OrgId);
        Assert.Equal(1, await db.SupplierServiceListings.CountAsync(l => l.OrgId == aOrg));
        Assert.Equal(1, await db.SupplierServiceListings.CountAsync(l => l.OrgId == bOrg));
    }

    [PostgresFact]
    public async Task ADualRoleAccount_UsesItsSupplierOrgForTheCatalog_NotItsHostOrg()
    {
        // A host that is also a supplier: User.OrgId is the host org, User.SupplierOrgId the supplier org. The tenant filter
        // follows the first, the catalog the second.
        var ownerId = $"auth0|sp02-dual-{Guid.NewGuid():N}";
        var hostOrg = await factory.SeedOrgForOwnerAsync(ownerId);
        Guid supplierOrgId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var email = $"sp02.dual.{Guid.NewGuid():N}@example.com";
            var supplierOrg = new OrgEntity
            {
                Name = "Fornitore doppio ruolo",
                Slug = $"supplier-{Guid.NewGuid():N}"[..30],
                DisplayName = "Fornitore doppio ruolo",
                ContactEmail = email,
                OrgType = OrgType.Supplier,
                PlanTier = PlanTier.Starter,
                IsActive = true,
            };
            db.Orgs.Add(supplierOrg);
            db.SupplierProfiles.Add(new SupplierProfile { OrgId = supplierOrg.Id, Email = email, LegalName = "Doppio Srl", Phone = "+39 06 030303" });
            var user = await db.Users.SingleAsync(u => u.Id == ownerId);
            user.SupplierOrgId = supplierOrg.Id;
            await db.SaveChangesAsync();
            supplierOrgId = supplierOrg.Id;
        }

        using var client = factory.CreateAuthenticatedClient(ownerId, roles: "Supplier,PropertyOwner");
        var created = await client.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadAsync(created)).GetProperty("id").GetGuid();
        Assert.Single(Ids(await ReadAsync(await client.GetAsync(Url))));
        await using var verify = factory.Services.CreateAsyncScope();
        var row = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == id);
        Assert.Equal(supplierOrgId, row.OrgId);
        Assert.NotEqual(hostOrg.Id, row.OrgId);
    }

    // ─── The unique slug, the checks and the cascade ─────────────────────────────

    [PostgresFact]
    public async Task SlugIndex_UniquePerSupplierAmongTheServicesNotDeleted()
    {
        var (_, orgA) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        var (_, orgB) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await SaveRowAsync(Row(orgA, "pulizia"));

        // The same slug twice for one supplier: the partial unique index refuses it.
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRowAsync(Row(orgA, "pulizia")));
        var postgres = Assert.IsType<PostgresException>(duplicate.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("UIX_SupplierServiceListings_OrgId_Slug", postgres.ConstraintName);

        // Another supplier is free to use it.
        await SaveRowAsync(Row(orgB, "pulizia"));

        // A deleted service frees the slug (the index covers only the rows that are not deleted)...
        await SaveRowAsync(Row(orgA, "vecchio", deletedAt: Newer));
        await SaveRowAsync(Row(orgA, "vecchio"));
        // ...and so do two deleted ones.
        await SaveRowAsync(Row(orgA, "vecchio", deletedAt: Newer));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(4, await db.SupplierServiceListings.CountAsync(l => l.OrgId == orgA));
    }

    [PostgresFact]
    public async Task Checks_TheDatabaseRefusesRowsTheRulesNeverWrite()
    {
        var (_, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        foreach (var (name, change) in new (string, Action<SupplierServiceListing>)[]
                 {
                     ("price zero", l => l.PriceFromCents = 0),
                     ("negative price", l => l.PriceFromCents = -100),
                     ("zero duration", l => l.DurationMinutes = 0),
                     ("negative notice", l => l.MinNoticeHours = -1),
                     ("weekday bits above sunday", l => l.WeekdaysMask = 128),
                     ("negative weekday mask", l => l.WeekdaysMask = -1),
                 })
        {
            var row = Row(orgId, $"check-{Guid.NewGuid():N}"[..20]);
            change(row);

            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveRowAsync(row));
            Assert.True(
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.CheckViolation },
                $"{name}: expected a check violation, got {ex.InnerException?.Message}");
        }

        // The accepted bounds are accepted.
        var ok = Row(orgId, "bounds");
        ok.PriceFromCents = null;
        ok.DurationMinutes = null;
        ok.MinNoticeHours = 0;
        ok.WeekdaysMask = 0;
        await SaveRowAsync(ok);
    }

    [PostgresFact]
    public async Task Table_IsAChildOfTheSupplierProfileInCascade_AndHasNoPhysicalXminColumn()
    {
        var (_, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await SaveRowAsync(Row(orgId, "uno"));
        await SaveRowAsync(Row(orgId, "due", deletedAt: Newer));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // xmin is a system column: the migration does not create one (the unique index is the only extra).
        var columns = await db.Database
            .SqlQuery<string>($"SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'SupplierServiceListings'")
            .ToListAsync();
        Assert.DoesNotContain("xmin", columns, StringComparer.Ordinal);
        Assert.Contains("PhotoUrlsJson", columns);

        var indexDefinitions = await db.Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'SupplierServiceListings' AND indexname = 'UIX_SupplierServiceListings_OrgId_Slug'")
            .ToListAsync();
        var definition = Assert.Single(indexDefinitions);
        Assert.Contains("UNIQUE", definition);
        Assert.Contains("\"DeletedAt\" IS NULL", definition);

        // Deleting the supplier profile takes its services with it.
        await db.SupplierProfiles.Where(sp => sp.OrgId == orgId).ExecuteDeleteAsync();
        Assert.Equal(0, await db.SupplierServiceListings.CountAsync(l => l.OrgId == orgId));
    }

    // ─── xmin ────────────────────────────────────────────────────────────────────

    [PostgresFact]
    public async Task Version_ChangesWithEveryUpdateOfTheRow_AndAnOlderOneIsRefused()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();
        var v1 = created.GetProperty("version").GetUInt32();

        var updated = await ReadAsync(await client.PutAsJsonAsync($"{Url}/{id}", new { name = "Pulizia 2", category = "cleaning", version = v1 }));
        var v2 = updated.GetProperty("version").GetUInt32();
        Assert.NotEqual(v1, v2);
        Assert.Equal(v2, (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("version").GetUInt32());

        // The version of before the update: 409, and nothing was written.
        var stale = await client.PutAsJsonAsync($"{Url}/{id}", new { name = "Vecchio", category = "cleaning", version = v1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("supplier_service_changed", (await ReadAsync(stale)).GetProperty("code").GetString());
        Assert.Equal("Pulizia 2", (await ReadAsync(await client.GetAsync($"{Url}/{id}"))).GetProperty("name").GetString());

        // A photo upload changes the row too, so the client that did not see it is told to reload.
        var uploaded = await ReadAsync(await UploadPhotoAsync(client, id));
        var v3 = uploaded.GetProperty("version").GetUInt32();
        Assert.NotEqual(v2, v3);
        var afterUpload = await client.PutAsJsonAsync($"{Url}/{id}", new { name = "Con foto", category = "cleaning", version = v2 });
        Assert.Equal(HttpStatusCode.Conflict, afterUpload.StatusCode);
        var withNew = await client.PutAsJsonAsync($"{Url}/{id}", new { name = "Con foto", category = "cleaning", version = v3 });
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);
    }

    // ─── The catalog lock: parallel requests ─────────────────────────────────────

    [PostgresFact]
    public async Task TwoParallelUpdatesWithTheSameVersion_OneWins_TheOtherGets409()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var client = SupplierClient(userId);
        var created = await ReadAsync(await client.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();
        var version = created.GetProperty("version").GetUInt32();

        var responses = await Task.WhenAll(
            client.PutAsJsonAsync($"{Url}/{id}", new { name = "Primo", category = "cleaning", version }),
            client.PutAsJsonAsync($"{Url}/{id}", new { name = "Secondo", category = "cleaning", version }));

        var statuses = responses.Select(r => r.StatusCode).Order().ToList();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, statuses);
        var winner = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
        var stored = await ReadAsync(await client.GetAsync($"{Url}/{id}"));
        Assert.Equal((await ReadAsync(winner)).GetProperty("name").GetString(), stored.GetProperty("name").GetString());
    }

    [PostgresFact]
    public async Task ParallelCreatesAtTheLimit_ExactlyOneTakesTheLastPlace()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < SupplierServiceCatalogLimits.MaxServicesPerSupplier - 1; i++)
                db.SupplierServiceListings.Add(Row(orgId, $"seed-{i}"));
            await db.SaveChangesAsync();
        }

        const int attempts = 8;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async i =>
        {
            using var client = SupplierClient(userId);
            return await client.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body($"Concorrente {i}"));
        }));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Equal(attempts - 1, refused.Count);
        foreach (var response in refused)
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("supplier_service_limit_reached", (await ReadAsync(response)).GetProperty("code").GetString());
        }

        await using var verify = factory.Services.CreateAsyncScope();
        var count = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierServiceListings.CountAsync(l => l.OrgId == orgId && l.DeletedAt == null);
        Assert.Equal(SupplierServiceCatalogLimits.MaxServicesPerSupplier, count);
    }

    [PostgresFact]
    public async Task ParallelCreatesWithTheSameName_GetDistinctSlugsAndNeverAnError()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);

        const int attempts = 8;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
        {
            using var client = SupplierClient(userId);
            return await client.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia"));
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var slugs = new List<string>();
        foreach (var response in responses)
            slugs.Add((await ReadAsync(response)).GetProperty("slug").GetString()!);
        Assert.Equal(attempts, slugs.Distinct().Count());
        Assert.Contains("pulizia", slugs);

        await using var verify = factory.Services.CreateAsyncScope();
        var sortOrders = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .SupplierServiceListings.Where(l => l.OrgId == orgId).Select(l => l.SortOrder).ToListAsync();
        Assert.Equal(attempts, sortOrders.Distinct().Count());
    }

    [PostgresFact]
    public async Task ParallelPhotoUploads_NoPhotoIsLost()
    {
        var (userId, _) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var setup = SupplierClient(userId);
        var created = await ReadAsync(await setup.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        const int attempts = 4;
        var responses = await Task.WhenAll(Enumerable.Range(0, attempts).Select(async _ =>
        {
            using var client = SupplierClient(userId);
            return await UploadPhotoAsync(client, id);
        }));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var photos = Strings((await ReadAsync(await setup.GetAsync($"{Url}/{id}"))).GetProperty("photoUrls"));
        Assert.Equal(attempts, photos.Count);
        Assert.Equal(attempts, photos.Distinct().Count());
    }

    [PostgresFact]
    public async Task ParallelPhotoUploadsOverTheLimit_NeverGoBeyondSixPhotos()
    {
        var (userId, orgId) = await SupplierCatalogTestData.SeedSupplierAsync(factory);
        using var setup = SupplierClient(userId);
        var created = await ReadAsync(await setup.PostAsJsonAsync(Url, SupplierServiceCatalogIntegrationTests.Body("Pulizia")));
        var id = created.GetProperty("id").GetGuid();

        var responses = await Task.WhenAll(Enumerable.Range(0, SupplierServiceCatalogLimits.MaxPhotos + 3).Select(async _ =>
        {
            using var client = SupplierClient(userId);
            return await UploadPhotoAsync(client, id);
        }));

        Assert.Equal(SupplierServiceCatalogLimits.MaxPhotos, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.All(
            responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        var photos = Strings((await ReadAsync(await setup.GetAsync($"{Url}/{id}"))).GetProperty("photoUrls"));
        Assert.Equal(SupplierServiceCatalogLimits.MaxPhotos, photos.Count);

        // The objects of the refused uploads were removed again: only the six photos are on the disk.
        var stored = Directory.GetFiles(factory.StorageRoot, "*.png", SearchOption.AllDirectories)
            .Count(path => path.Contains(orgId.ToString()));
        Assert.Equal(SupplierServiceCatalogLimits.MaxPhotos, stored);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private HttpClient SupplierClient(string userId) => factory.CreateAuthenticatedClient(userId, roles: "Supplier");

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static Guid[] Ids(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()!).ToList();

    private static async Task<HttpResponseMessage> UploadPhotoAsync(HttpClient client, Guid serviceId)
    {
        using var form = new MultipartFormDataContent();
        var png = new ByteArrayContent(PngBytes);
        png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(png, "photos", "foto.png");
        return await client.PostAsync($"{Url}/{serviceId}/photos", form);
    }

    private static SupplierServiceListing Row(Guid orgId, string slug, DateTime? deletedAt = null) =>
        SupplierCatalogTestData.Row(orgId, slug, deletedAt);

    private Task SaveRowAsync(SupplierServiceListing row) => SupplierCatalogTestData.SaveRowAsync(factory, row);
}
