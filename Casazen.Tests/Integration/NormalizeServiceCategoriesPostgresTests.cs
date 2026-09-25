using System.Text.Json;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Migrations;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SU-03 data migration (<see cref="NormalizeServiceCategories"/>) on real PostgreSQL: applies every migration up to
/// the one before it, stores categories the way the pre-SU-03 clients did (Italian labels from the supplier wizard,
/// mixed case, the app's <c>check-in</c>), then migrates. Known labels become codes, the host finds the supplier by
/// code, and values that cannot be mapped are kept and reported, never deleted.
/// </summary>
public class NormalizeServiceCategoriesPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync() => _database = await PostgresTestDatabase.CreateAsync();

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    [PostgresFact]
    public async Task Migration_SupplierSavedWithPulizieBeforeMigration_IsFoundByHostFilteringCleaning()
    {
        var s = new Seed();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await InsertPreSu03StateAsync(db, s);
            await db.Database.MigrateAsync();
        }

        await using var after = _database.CreateContext();
        var found = await CreateSupplierService(after).GetActiveByComune("Roma", ServiceCategories.Cleaning);
        Assert.Contains(found, sp => sp.OrgId == s.PulizieSupplier);
        Assert.DoesNotContain(found, sp => sp.OrgId == s.GardeningSupplier);
        Assert.Contains(await CreateSupplierService(after).GetActiveByComune("Roma", ServiceCategories.Gardening), sp => sp.OrgId == s.GardeningSupplier);

        // Labels mapped, duplicates removed (first occurrence order), unmapped value kept.
        Assert.Equal(new[] { "cleaning", "maintenance", "Idraulica speciale" }, await CategoriesAsync(after, s.PulizieSupplier));
        Assert.Equal(new[] { "gardening", "events", "rental", "excursions", "plumbing", "laundry", "check-in" }, await CategoriesAsync(after, s.GardeningSupplier));

        Assert.Equal("""["cleaning", "rental"]""", await InviteCategoriesAsync(after, s.Invite));
        Assert.Null(await InviteCategoriesAsync(after, s.InviteWithoutCategories));

        var requests = await after.ServiceRequests.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Category);
        Assert.Equal("cleaning", requests[s.PulizieRequest]);
        Assert.Equal("check-in", requests[s.CheckInRequest]);
        Assert.Equal("maintenance", requests[s.MaintenanceRequest]);
        Assert.Equal("foo", requests[s.UnmappedRequest]);

        var unmapped = await CreateSupplierService(after).GetUnmappedCategoriesAsync();
        Assert.Equal(
            new[]
            {
                new UnmappedServiceCategory("supplier_profile", s.PulizieSupplier, "Idraulica speciale"),
                new UnmappedServiceCategory("service_request", s.UnmappedRequest, "foo"),
            },
            unmapped);
        Assert.Empty(await after.Database.GetPendingMigrationsAsync());
    }

    [PostgresFact]
    public async Task NormalizeSql_RunTwice_ChangesNothingTheSecondTime()
    {
        var s = new Seed();
        await using (var db = _database!.CreateContext())
        {
            db.GetService<IMigrator>().Migrate(PreviousMigration(db));
            await InsertPreSu03StateAsync(db, s);
            await db.Database.MigrateAsync();
        }

        await using var again = _database.CreateContext();
        var before = await SnapshotAsync(again);
        await again.Database.ExecuteSqlRawAsync(NormalizeServiceCategories.NormalizeSql);

        Assert.Equal(before, await SnapshotAsync(again));
    }

    private sealed class Seed
    {
        public Guid HostOrg { get; } = Guid.NewGuid();
        public Guid Property { get; } = Guid.NewGuid();
        public Guid PulizieSupplier { get; } = Guid.NewGuid();
        public Guid GardeningSupplier { get; } = Guid.NewGuid();
        public Guid Invite { get; } = Guid.NewGuid();
        public Guid InviteWithoutCategories { get; } = Guid.NewGuid();
        public Guid PulizieRequest { get; } = Guid.NewGuid();
        public Guid CheckInRequest { get; } = Guid.NewGuid();
        public Guid MaintenanceRequest { get; } = Guid.NewGuid();
        public Guid UnmappedRequest { get; } = Guid.NewGuid();
    }

    /// <summary>Rows as the pre-SU-03 clients wrote them (schema right before NormalizeServiceCategories).</summary>
    private static async Task InsertPreSu03StateAsync(AppDbContext db, Seed s)
    {
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "Orgs" ("Id", "Name", "Slug", "PlanTier", "DisplayName", "ContactEmail", "IsActive", "CreatedAt", "UpdatedAt", "OrgType")
            VALUES
              ({s.HostOrg}, 'Host', {"su03-host-" + s.HostOrg.ToString("N")}, 0, 'Host', '', true, now(), now(), 0),
              ({s.PulizieSupplier}, 'Pulizie Srl', {"su03-a-" + s.PulizieSupplier.ToString("N")}, 0, 'Pulizie Srl', '', true, now(), now(), 1),
              ({s.GardeningSupplier}, 'Verde Srl', {"su03-b-" + s.GardeningSupplier.ToString("N")}, 0, 'Verde Srl', '', true, now(), now(), 1);

            INSERT INTO "SupplierProfiles" ("OrgId", "Status", "LegalName", "Phone", "Email", "CategoriesJson", "ComuniJson", "PhotoUrlsJson", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.PulizieSupplier}, 1, 'Pulizie Srl', '+39 06 1', 'a@test.com',
               '["Pulizie", "Manutenzione", " pulizie ", "Idraulica speciale"]'::jsonb, '["058091"]'::jsonb, '[]'::jsonb, now(), now()),
              ({s.GardeningSupplier}, 1, 'Verde Srl', '+39 06 2', 'b@test.com',
               '["Giardinaggio", "Eventi", "Noleggio", "Escursioni", "Idraulico", "LAVANDERIA", "check-in"]'::jsonb, '["058091"]'::jsonb, '[]'::jsonb, now(), now());

            INSERT INTO "SupplierInviteRecords" ("Id", "Email", "ComuneCode", "CategoriesJson", "IsUsed", "ExpiresAt", "CreatedAt")
            VALUES
              ({s.Invite}, 'invite@test.com', 'H501', '["Pulizie", "Noleggio"]'::jsonb, false, now() + interval '7 days', now()),
              ({s.InviteWithoutCategories}, 'invite2@test.com', 'H501', NULL, false, now() + interval '7 days', now());

            INSERT INTO "Properties" (
                "Id", "OwnerId", "OrgId", "Name", "Description", "Address", "City", "PostalCode",
                "Latitude", "Longitude", "Bedrooms", "Bathrooms", "MaxGuests", "NightlyRate", "CleaningFee", "DamageDeposit",
                "Amenities", "PhotoUrls", "HouseRules", "Timezone", "IsActive", "CreatedAt", "UpdatedAt")
            VALUES ({s.Property}, 'auth0|su03-host', {s.HostOrg}, 'Casa Roma', '', 'Via Roma 1', 'Roma', '00100',
                    0, 0, 1, 1, 2, 100, 0, 0, ARRAY[]::integer[], ARRAY[]::text[], '', 'Europe/Rome', true, now(), now());

            INSERT INTO "ServiceRequests" ("Id", "OrgId", "PropertyId", "SupplierOrgId", "Category", "Urgency", "Notes", "Status", "ChargeToGuest", "CreatedAt", "UpdatedAt")
            VALUES
              ({s.PulizieRequest}, {s.HostOrg}, {s.Property}, {s.PulizieSupplier}, 'Pulizie', 0, '', 0, false, now() - interval '3 minutes', now()),
              ({s.CheckInRequest}, {s.HostOrg}, {s.Property}, {s.PulizieSupplier}, 'Check-in', 0, '', 0, false, now() - interval '2 minutes', now()),
              ({s.MaintenanceRequest}, {s.HostOrg}, {s.Property}, {s.PulizieSupplier}, 'maintenance', 0, '', 0, false, now() - interval '1 minute', now()),
              ({s.UnmappedRequest}, {s.HostOrg}, {s.Property}, {s.PulizieSupplier}, 'foo', 0, '', 0, false, now(), now());
            """);
    }

    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_NormalizeServiceCategories", StringComparison.Ordinal));
        Assert.True(index > 0, "NormalizeServiceCategories migration not found.");
        return all[index - 1];
    }

    private static SupplierService CreateSupplierService(AppDbContext db) =>
        new(
            db,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Mock.Of<ISafeExternalHttpClient>(),
            Options.Create(new SupplierRegistrationOptions()),
            NullLogger<SupplierService>.Instance);

    private static async Task<string[]> CategoriesAsync(AppDbContext db, Guid supplierOrgId)
    {
        var json = await db.SupplierProfiles.AsNoTracking()
            .Where(sp => sp.OrgId == supplierOrgId)
            .Select(sp => sp.CategoriesJson)
            .SingleAsync();
        return JsonSerializer.Deserialize<string[]>(json)!;
    }

    private static Task<string?> InviteCategoriesAsync(AppDbContext db, Guid inviteId) =>
        db.SupplierInviteRecords.AsNoTracking().Where(i => i.Id == inviteId).Select(i => i.CategoriesJson).SingleAsync();

    private static async Task<string> SnapshotAsync(AppDbContext db)
    {
        var profiles = await db.SupplierProfiles.AsNoTracking().OrderBy(p => p.OrgId).Select(p => p.OrgId + p.CategoriesJson + p.UpdatedAt).ToListAsync();
        var invites = await db.SupplierInviteRecords.AsNoTracking().OrderBy(i => i.Id).Select(i => i.Id + (i.CategoriesJson ?? "null")).ToListAsync();
        var requests = await db.ServiceRequests.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Id + r.Category + r.UpdatedAt).ToListAsync();
        return string.Join("|", profiles.Concat(invites).Concat(requests));
    }
}
