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
/// Admin repair <c>POST /api/admin/suppliers/fix-orphaned</c> (SU-14, A4-22) on PostgreSQL. Profiles with the same email
/// are merged into one keeper in one transaction (service requests, availability, categories, comuni, accounts and
/// devices move; never a 500 on the <c>ServiceRequests</c> foreign key), a dry run changes nothing, and nothing is ever
/// linked by email (A4-23). The unique email index of the migration <c>SupplierProfileEmailUnique</c> forbids
/// duplicates, so each test first drops it to reproduce the data written before it.
/// </summary>
/// <remarks>The repair spans every org: each test asserts only on the rows it seeded.</remarks>
public class SupplierRepairPostgresTests(CasazenWebApplicationFactory factory) : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly DateTime Older = new(2026, 1, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 3, 10, 9, 0, 0, DateTimeKind.Utc);

    [PostgresFact]
    public async Task FixOrphaned_DuplicateWithServiceRequests_MovesRequestsToKeeperAndDeletesDuplicate()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("requests");
        var property = await factory.SeedPropertyAsync($"auth0|su14-host-{Guid.NewGuid():N}");
        // The keeper is the active profile; the duplicate is older, pending, held by nobody, and has the requests.
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer, """["maintenance"]""", """["H501"]""");
        var duplicate = await SeedProfileAsync(
            $"  {email.ToUpperInvariant()} ", SupplierStatus.Pending, Older, """["cleaning","maintenance"]""", """["F205"]""");
        var keeperUser = await SeedUserAsync(email, orgId: keeper, supplierOrgId: keeper);
        var requested = await SeedRequestAsync(property, duplicate, ServiceRequestStatus.Richiesto);
        var completed = await SeedRequestAsync(property, duplicate, ServiceRequestStatus.Completato);
        await SeedAvailabilityAsync(duplicate, new DateOnly(2026, 10, 1), available: true);
        await SeedAvailabilityAsync(duplicate, new DateOnly(2026, 10, 2), available: false);
        await SeedAvailabilityAsync(keeper, new DateOnly(2026, 10, 2), available: true);

        var response = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(report.GetProperty("dryRun").GetBoolean());
        var merge = SingleMerge(report, duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(2, merge.GetProperty("serviceRequestsMoved").GetInt32());
        Assert.Equal(1, merge.GetProperty("availabilityDaysMoved").GetInt32());
        Assert.Equal(1, merge.GetProperty("availabilityDaysDropped").GetInt32());
        Assert.Equal(new[] { "cleaning" }, Strings(merge.GetProperty("categoriesAdded")));
        Assert.Equal(new[] { "F205" }, Strings(merge.GetProperty("comuniAdded")));
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var requests = await db.ServiceRequests.AsNoTracking()
            .Where(sr => sr.Id == requested || sr.Id == completed)
            .ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, sr => Assert.Equal(keeper, sr.SupplierOrgId));
        Assert.Contains(requests, sr => sr.Id == completed && sr.Status == ServiceRequestStatus.Completato);
        Assert.False(await db.SupplierProfiles.AnyAsync(sp => sp.OrgId == duplicate));
        Assert.False(await db.Orgs.AnyAsync(o => o.Id == duplicate));
        var kept = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == keeper);
        Assert.Equal(new[] { "maintenance", "cleaning" }, JsonSerializer.Deserialize<string[]>(kept.CategoriesJson));
        Assert.Equal(new[] { "H501", "F205" }, JsonSerializer.Deserialize<string[]>(kept.ComuniJson));
        var days = await db.SupplierAvailability.AsNoTracking()
            .Where(a => a.OrgId == keeper || a.OrgId == duplicate)
            .OrderBy(a => a.Date)
            .ToListAsync();
        Assert.Equal(2, days.Count);
        Assert.All(days, a => Assert.Equal(keeper, a.OrgId));
        // The keeper's day wins: the keeper is the profile in use.
        Assert.True(days.Single(a => a.Date == new DateOnly(2026, 10, 2)).Available);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == keeperUser);
        Assert.Equal(keeper, user.SupplierOrgId);
    }

    [PostgresFact]
    public async Task FixOrphaned_DuplicateHeldByAccounts_MovesAccountLinksAndDevicesToKeeper()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("accounts");
        var hostOrg = await factory.SeedOrgForOwnerAsync($"auth0|su14-dual-{Guid.NewGuid():N}");
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        // Only the duplicate is held, by a supplier-only account (its org is the duplicate) and by a dual-role host
        // (its supplier link is the duplicate): one held profile, so the merge decides alone.
        var supplierOnly = await SeedUserAsync(email, orgId: duplicate, supplierOrgId: null);
        var dualRole = await SeedUserAsync(email, orgId: hostOrg.Id, supplierOrgId: duplicate);
        var device = await SeedDeviceAsync(supplierOnly, duplicate);

        var response = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var merge = SingleMerge(await response.Content.ReadFromJsonAsync<JsonElement>(), duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(2, merge.GetProperty("supplierLinksMoved").GetInt32());
        Assert.Equal(1, merge.GetProperty("orgMembersMoved").GetInt32());
        Assert.Equal(1, merge.GetProperty("devicesMoved").GetInt32());
        Assert.True(merge.GetProperty("duplicateOrgDeleted").GetBoolean());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var supplier = await db.Users.AsNoTracking().SingleAsync(u => u.Id == supplierOnly);
        Assert.Equal(keeper, supplier.OrgId);
        Assert.Equal(keeper, supplier.SupplierOrgId);
        var host = await db.Users.AsNoTracking().SingleAsync(u => u.Id == dualRole);
        Assert.Equal(hostOrg.Id, host.OrgId);
        Assert.Equal(keeper, host.SupplierOrgId);
        Assert.Equal(keeper, (await db.DeviceRegistrations.AsNoTracking().SingleAsync(d => d.Id == device)).OrgId);
        Assert.False(await db.Orgs.AnyAsync(o => o.Id == duplicate));
        // No account is left pointing to the deleted org.
        Assert.False(await db.Users.AnyAsync(u => u.OrgId == duplicate || u.SupplierOrgId == duplicate));
    }

    [PostgresFact]
    public async Task FixOrphaned_NoActiveProfile_KeepsTheProfileHeldByAnAccount()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("held");
        var hostOrg = await factory.SeedOrgForOwnerAsync($"auth0|su14-held-host-{Guid.NewGuid():N}");
        var older = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        var held = await SeedProfileAsync(email, SupplierStatus.Pending, Newer);
        var dualRole = await SeedUserAsync(email, orgId: hostOrg.Id, supplierOrgId: held);

        var response = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Neither is active: the profile an account holds wins over the older one.
        var merge = SingleMerge(await response.Content.ReadFromJsonAsync<JsonElement>(), older);
        Assert.Equal(held, merge.GetProperty("keeperOrgId").GetGuid());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == dualRole);
        Assert.Equal(hostOrg.Id, user.OrgId);
        Assert.Equal(held, user.SupplierOrgId);
        Assert.False(await db.SupplierProfiles.AnyAsync(sp => sp.OrgId == older));
    }

    [PostgresFact]
    public async Task FixOrphaned_DuplicateOrgWithHostData_KeepsOrgWithoutProfileAndNever500()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("hostdata");
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Older);
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Newer);
        // A supplier-only account that then completed the host onboarding on its supplier org (A1-40): a property
        // references the duplicate org (ON DELETE RESTRICT).
        var owner = await SeedUserAsync(email, orgId: duplicate, supplierOrgId: null);
        var property = await SeedPropertyInOrgAsync(duplicate, owner);

        var response = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var merge = SingleMerge(await response.Content.ReadFromJsonAsync<JsonElement>(), duplicate);
        Assert.False(merge.GetProperty("duplicateOrgDeleted").GetBoolean());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.Orgs.AnyAsync(o => o.Id == duplicate));
        Assert.False(await db.SupplierProfiles.AnyAsync(sp => sp.OrgId == duplicate));
        Assert.True(await db.Properties.IgnoreQueryFilters([AppDbContext.TenantQueryFilter]).AnyAsync(p => p.Id == property));
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == owner);
        Assert.Equal(duplicate, user.OrgId);
        Assert.Equal(keeper, user.SupplierOrgId);
    }

    [PostgresFact]
    public async Task FixOrphaned_DryRun_ReportsTheMergeAndChangesNothing()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("dryrun");
        var property = await factory.SeedPropertyAsync($"auth0|su14-dry-{Guid.NewGuid():N}");
        var keeper = await SeedProfileAsync(email, SupplierStatus.Active, Newer, """["maintenance"]""");
        var duplicate = await SeedProfileAsync(email, SupplierStatus.Pending, Older, """["cleaning"]""");
        var supplierOnly = await SeedUserAsync(email, orgId: duplicate, supplierOrgId: duplicate);
        var request = await SeedRequestAsync(property, duplicate, ServiceRequestStatus.Richiesto);
        var dangling = await SeedUserAsync(NewEmail("dangling-dry"), orgId: null, supplierOrgId: Guid.NewGuid());

        // No query string: the endpoint defaults to a dry run.
        using var admin = factory.CreateAuthenticatedClient($"auth0|su14-admin-{Guid.NewGuid():N}", roles: "Admin");
        var response = await admin.PostAsync("/api/admin/suppliers/fix-orphaned", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        var merge = SingleMerge(report, duplicate);
        Assert.Equal(keeper, merge.GetProperty("keeperOrgId").GetGuid());
        Assert.Equal(1, merge.GetProperty("serviceRequestsMoved").GetInt32());
        Assert.Contains(dangling, Strings(report.GetProperty("danglingLinksCleared")));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.SupplierProfiles.AnyAsync(sp => sp.OrgId == duplicate));
        Assert.True(await db.Orgs.AnyAsync(o => o.Id == duplicate));
        Assert.Equal(duplicate, (await db.ServiceRequests.AsNoTracking().SingleAsync(sr => sr.Id == request)).SupplierOrgId);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == supplierOnly);
        Assert.Equal(duplicate, user.OrgId);
        Assert.Equal(duplicate, user.SupplierOrgId);
        Assert.NotNull((await db.Users.AsNoTracking().SingleAsync(u => u.Id == dangling)).SupplierOrgId);
        var kept = await db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == keeper);
        Assert.Equal("""["maintenance"]""", kept.CategoriesJson);
    }

    [PostgresFact]
    public async Task FixOrphaned_ProfilesHeldBySeveralAccounts_ReportsManualInterventionAndChangesNothing()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("several");
        var first = await SeedProfileAsync(email, SupplierStatus.Active, Older);
        var second = await SeedProfileAsync(email, SupplierStatus.Pending, Newer);
        var firstUser = await SeedUserAsync(email, orgId: first, supplierOrgId: first);
        var secondUser = await SeedUserAsync(email.ToUpperInvariant(), orgId: second, supplierOrgId: second);

        var response = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == first || m.GetProperty("duplicateOrgId").GetGuid() == second);
        var manual = SingleManual(report, first);
        Assert.Equal("supplier_duplicate_several_accounts", manual.GetProperty("code").GetString());
        Assert.Contains(second, Guids(manual.GetProperty("orgIds")));
        Assert.Equal(new[] { firstUser, secondUser }.Order(StringComparer.Ordinal), Strings(manual.GetProperty("userIds")));

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.SupplierProfiles.CountAsync(sp => sp.OrgId == first || sp.OrgId == second));
        Assert.Equal(first, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == firstUser)).SupplierOrgId);
        Assert.Equal(second, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == secondUser)).SupplierOrgId);
    }

    [PostgresFact]
    public async Task FixOrphaned_SuspendedProfileInGroup_IsNotMerged()
    {
        await DropEmailIndexAsync();
        var email = NewEmail("suspended");
        var active = await SeedProfileAsync(email, SupplierStatus.Active, Older);
        var suspended = await SeedProfileAsync(email, SupplierStatus.Suspended, Newer);

        var report = await (await FixOrphanedAsync(dryRun: false)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("supplier_duplicate_suspended", SingleManual(report, suspended).GetProperty("code").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.SupplierProfiles.CountAsync(sp => sp.OrgId == active || sp.OrgId == suspended));
    }

    [PostgresFact]
    public async Task FixOrphaned_UnheldProfileWithAccountOfSameEmail_IsNotLinkedAndNeedsClaim()
    {
        var email = NewEmail("ambiguous");
        var profile = await SeedProfileAsync(email, SupplierStatus.Pending, Older);
        // An account that only shows the same email (in another case): the email is not proven, so no link (A4-23).
        var account = await SeedUserAsync($" {email.ToUpperInvariant()}", orgId: null, supplierOrgId: null);
        var orphan = await SeedProfileAsync(NewEmail("orphan"), SupplierStatus.Pending, Older);

        var response = await FixOrphanedAsync(dryRun: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        var manual = SingleManual(report, profile);
        Assert.Equal("supplier_link_requires_claim", manual.GetProperty("code").GetString());
        Assert.Equal(new[] { account }, Strings(manual.GetProperty("userIds")));
        var orphans = Guids(report.GetProperty("orphanProfiles"));
        Assert.Contains(orphan, orphans);
        Assert.DoesNotContain(profile, orphans);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == account);
        Assert.Null(user.SupplierOrgId);
        Assert.Null(user.OrgId);
        Assert.True(await db.SupplierProfiles.AnyAsync(sp => sp.OrgId == profile));
    }

    [PostgresFact]
    public async Task FixOrphaned_AccountLinkedToDeletedOrgOrOwnSupplierOrg_RepairsOnlyItsOwnLinks()
    {
        var dangling = await SeedUserAsync(NewEmail("dangling"), orgId: null, supplierOrgId: Guid.NewGuid());
        var ownOrg = await SeedProfileAsync(NewEmail("own"), SupplierStatus.Active, Older);
        var supplierOnly = await SeedUserAsync(NewEmail("own-user"), orgId: ownOrg, supplierOrgId: null);

        var report = await (await FixOrphanedAsync(dryRun: false)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(dangling, Strings(report.GetProperty("danglingLinksCleared")));
        Assert.Contains(supplierOnly, Strings(report.GetProperty("supplierLinksBackfilled")));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Null((await db.Users.AsNoTracking().SingleAsync(u => u.Id == dangling)).SupplierOrgId);
        Assert.Equal(ownOrg, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == supplierOnly)).SupplierOrgId);
    }

    [PostgresFact]
    public async Task FixOrphaned_BlankEmailProfiles_AreNeverMerged()
    {
        var first = await SeedProfileAsync(string.Empty, SupplierStatus.Pending, Older);
        var second = await SeedProfileAsync("   ", SupplierStatus.Pending, Newer);
        await SeedUserAsync(string.Empty, orgId: null, supplierOrgId: first);
        await SeedUserAsync(string.Empty, orgId: null, supplierOrgId: second);

        var report = await (await FixOrphanedAsync(dryRun: false)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == first || m.GetProperty("duplicateOrgId").GetGuid() == second);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, await db.SupplierProfiles.CountAsync(sp => sp.OrgId == first || sp.OrgId == second));
    }

    [PostgresFact]
    public async Task FixOrphaned_NotAnAdmin_Returns403()
    {
        using var supplier = factory.CreateAuthenticatedClient($"auth0|su14-not-admin-{Guid.NewGuid():N}", roles: "Supplier");

        var response = await supplier.PostAsync("/api/admin/suppliers/fix-orphaned?dryRun=false", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string NewEmail(string prefix) => $"su14-{prefix}-{Guid.NewGuid():N}@test.com";

    private async Task<HttpResponseMessage> FixOrphanedAsync(bool dryRun)
    {
        using var admin = factory.CreateAuthenticatedClient($"auth0|su14-admin-{Guid.NewGuid():N}", roles: "Admin");
        return await admin.PostAsync($"/api/admin/suppliers/fix-orphaned?dryRun={(dryRun ? "true" : "false")}", null);
    }

    /// <summary>Data written before the migration SupplierProfileEmailUnique: without its unique index.</summary>
    private async Task DropEmailIndexAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"DROP INDEX IF EXISTS \"{SupplierProfileEmailIndex.Name}\"");
    }

    private async Task<Guid> SeedProfileAsync(
        string email,
        SupplierStatus status,
        DateTime createdAt,
        string categoriesJson = "[]",
        string comuniJson = """["H501"]""")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = "Fornitore SU-14",
            Slug = $"su14-{Guid.NewGuid():N}",
            DisplayName = "Fornitore SU-14",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = "Fornitore SU-14 Srl",
            Phone = "+39 06 140140",
            Status = status,
            CategoriesJson = categoriesJson,
            ComuniJson = comuniJson,
            TosAcceptedAt = status == SupplierStatus.Active ? createdAt : null,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return org.Id;
    }

    private async Task<string> SeedUserAsync(string email, Guid? orgId, Guid? supplierOrgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = new User
        {
            Id = $"auth0|su14-{Guid.NewGuid():N}",
            Email = email,
            FirstName = "Fornitore",
            LastName = "Test",
            Role = UserRole.Supplier,
            OrgId = orgId,
            SupplierOrgId = supplierOrgId,
            IsActive = true,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedRequestAsync(Property property, Guid supplierOrgId, ServiceRequestStatus status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = new ServiceRequest
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            SupplierOrgId = supplierOrgId,
            Category = "cleaning",
            Status = status,
            // Long-rent: tied to the property only, no stay needed (D2).
            RentalContext = ServiceRequestRentalContext.LongRent,
            CompletedAt = status == ServiceRequestStatus.Completato ? DateTime.UtcNow : null,
        };
        db.ServiceRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task SeedAvailabilityAsync(Guid orgId, DateOnly date, bool available)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierAvailability.Add(new SupplierAvailability { OrgId = orgId, Date = date, Available = available });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedDeviceAsync(string userId, Guid orgId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var device = new DeviceRegistration
        {
            UserId = userId,
            OrgId = orgId,
            Platform = "ios",
            PushToken = $"ExponentPushToken[{Guid.NewGuid():N}]",
            DeviceId = Guid.NewGuid().ToString("N"),
        };
        db.DeviceRegistrations.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
    }

    private async Task<Guid> SeedPropertyInOrgAsync(Guid orgId, string ownerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var property = new Property
        {
            OwnerId = ownerId,
            OrgId = orgId,
            Name = "Casa del fornitore",
            Description = "SU-14",
            Address = $"Via Fornitore {Guid.NewGuid():N}",
            City = "Roma",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            IsActive = true,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static JsonElement SingleMerge(JsonElement report, Guid duplicateOrgId) =>
        Assert.Single(
            report.GetProperty("merges").EnumerateArray(),
            m => m.GetProperty("duplicateOrgId").GetGuid() == duplicateOrgId);

    private static JsonElement SingleManual(JsonElement report, Guid orgId) =>
        Assert.Single(
            report.GetProperty("manualInterventions").EnumerateArray(),
            m => Guids(m.GetProperty("orgIds")).Contains(orgId));

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToList();

    private static List<Guid> Guids(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetGuid()).ToList();
}
