using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-05 data migration <see cref="SeparateSupplierOrgFromHostOrgId"/> on PostgreSQL (A1-40): <c>User.OrgId</c> becomes
/// the host org only. A supplier-only account loses the <c>OrgId</c> link and keeps <c>SupplierOrgId</c>; a supplier org
/// that already holds host data becomes the Host org in place (no tenant row moves) and its supplier side moves to a new
/// supplier org. Nothing is deleted.
/// </summary>
public class SeparateSupplierOrgFromHostOrgIdMigrationPostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync("pl05");
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private AppDbContext NewContext() => _database!.CreateContext();

    [PostgresFact]
    public async Task SeparateSql_SupplierOnlyAccount_ClearsOrgIdAndKeepsSupplierLink()
    {
        var supplierOrg = NewOrg("clean", OrgType.Supplier);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(supplierOrg);
            seed.SupplierProfiles.Add(NewProfile(supplierOrg.Id, "clean"));
            seed.Users.Add(NewUser("clean", u =>
            {
                u.OrgId = supplierOrg.Id;
                u.SupplierOrgId = supplierOrg.Id;
            }));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("clean");
        Assert.Null(user.OrgId);
        Assert.Equal(supplierOrg.Id, user.SupplierOrgId);
        await using var check = NewContext();
        Assert.Equal(OrgType.Supplier, (await check.Orgs.AsNoTracking().SingleAsync(o => o.Id == supplierOrg.Id)).OrgType);
    }

    [PostgresFact]
    public async Task SeparateSql_LegacyAccountWithoutSupplierOrgId_BackfillsSupplierLinkThenClearsOrgId()
    {
        var supplierOrg = NewOrg("legacy", OrgType.Supplier);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(supplierOrg);
            seed.SupplierProfiles.Add(NewProfile(supplierOrg.Id, "legacy"));
            // Pre-SU-08 shape: only OrgId was ever set.
            seed.Users.Add(NewUser("legacy", u => u.OrgId = supplierOrg.Id));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("legacy");
        Assert.Null(user.OrgId);
        Assert.Equal(supplierOrg.Id, user.SupplierOrgId);
    }

    [PostgresFact]
    public async Task SeparateSql_AccountWhoseSupplierLinkIsAnotherOrg_KeepsOrgIdSoTheProfileStaysHeld()
    {
        var heldByOrgId = NewOrg("held", OrgType.Supplier);
        var other = NewOrg("other", OrgType.Supplier);
        await using (var seed = NewContext())
        {
            seed.Orgs.AddRange(heldByOrgId, other);
            seed.SupplierProfiles.AddRange(NewProfile(heldByOrgId.Id, "held"), NewProfile(other.Id, "other"));
            seed.Users.Add(NewUser("two-links", u =>
            {
                u.OrgId = heldByOrgId.Id;
                u.SupplierOrgId = other.Id;
            }));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("two-links");
        Assert.Equal(heldByOrgId.Id, user.OrgId);
        Assert.Equal(other.Id, user.SupplierOrgId);
    }

    [PostgresFact]
    public async Task SeparateSql_SupplierOrgWithHostData_BecomesHostOrgAndSupplierSideMovesToNewOrg()
    {
        // A1-40 ran to completion before the fix: the supplier did the host onboarding on its supplier org and created a
        // property there; meanwhile another host sent it a request.
        var mixed = NewOrg("mixed", OrgType.Supplier);
        var otherHost = NewOrg("other-host", OrgType.Host);
        var profile = NewProfile(mixed.Id, "mixed");
        profile.ShowcaseSlug = $"pulizie-{Guid.NewGuid():N}"[..20];
        var owner = NewUser("mixed", u =>
        {
            u.OrgId = mixed.Id;
            u.SupplierOrgId = mixed.Id;
        });
        var hostProperty = NewProperty(mixed.Id, owner.Id);
        var otherProperty = NewProperty(otherHost.Id, "auth0|mig-other-host");
        var request = new ServiceRequest
        {
            OrgId = otherHost.Id,
            PropertyId = otherProperty.Id,
            SupplierOrgId = mixed.Id,
            Category = "cleaning",
            RentalContext = ServiceRequestRentalContext.LongRent,
        };
        await using (var seed = NewContext())
        {
            seed.Orgs.AddRange(mixed, otherHost);
            seed.SupplierProfiles.Add(profile);
            seed.Users.Add(owner);
            seed.Properties.AddRange(hostProperty, otherProperty);
            seed.SupplierAvailability.Add(new SupplierAvailability
            {
                OrgId = mixed.Id,
                Date = new DateOnly(2026, 11, 3),
                Available = false,
            });
            seed.ConsentRecords.Add(new ConsentRecord
            {
                UserId = owner.Id,
                OrgId = mixed.Id,
                Type = ConsentType.Tos,
                Version = "2026-06-v1",
                RecordedAt = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc),
            });
            seed.ServiceRequests.Add(request);
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        await using var check = NewContext();
        // The host side stays where it is: same org, now typed Host, same property, consents and account link.
        Assert.Equal(OrgType.Host, (await check.Orgs.AsNoTracking().SingleAsync(o => o.Id == mixed.Id)).OrgType);
        Assert.Equal(mixed.Id, (await check.Properties.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(p => p.Id == hostProperty.Id)).OrgId);
        Assert.True(await check.ConsentRecords.IgnoreQueryFilters().AnyAsync(c => c.OrgId == mixed.Id));
        var user = await GetUserAsync("mixed");
        Assert.Equal(mixed.Id, user.OrgId);

        // The supplier side is on a new supplier org, with the profile (and its showcase slug), days and requests.
        var supplierOrgId = Assert.IsType<Guid>(user.SupplierOrgId);
        Assert.NotEqual(mixed.Id, supplierOrgId);
        var supplierOrg = await check.Orgs.AsNoTracking().SingleAsync(o => o.Id == supplierOrgId);
        Assert.Equal(OrgType.Supplier, supplierOrg.OrgType);
        Assert.Equal(PlanTier.Starter, supplierOrg.PlanTier);
        Assert.StartsWith("supplier-", supplierOrg.Slug);
        Assert.Equal(profile.LegalName, supplierOrg.Name);
        Assert.Null(supplierOrg.StripeCustomerId);
        var movedProfile = await check.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == supplierOrgId);
        Assert.Equal(profile.ShowcaseSlug, movedProfile.ShowcaseSlug);
        Assert.False(await check.SupplierProfiles.AnyAsync(sp => sp.OrgId == mixed.Id));
        Assert.Equal(supplierOrgId, (await check.SupplierAvailability.AsNoTracking().SingleAsync()).OrgId);
        var movedRequest = await check.ServiceRequests.AsNoTracking().SingleAsync(sr => sr.Id == request.Id);
        Assert.Equal(supplierOrgId, movedRequest.SupplierOrgId);
        Assert.Equal(otherHost.Id, movedRequest.OrgId);

        // Idempotent: a second run finds no supplier org with host data and changes nothing.
        var orgsBefore = await check.Orgs.CountAsync();
        await RunMigrationSqlAsync();
        await using var again = NewContext();
        Assert.Equal(orgsBefore, await again.Orgs.CountAsync());
        Assert.Equal(supplierOrgId, (await GetUserAsync("mixed")).SupplierOrgId);
    }

    [PostgresFact]
    public async Task SeparateSql_SupplierOrgWithStripeCustomerOnly_IsKeptAsHostOrg()
    {
        // A paid host plan bought on the supplier org: billing is host data, the org must not be detached from its account.
        var mixed = NewOrg("billing", OrgType.Supplier);
        mixed.StripeCustomerId = "cus_pl05test";
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(mixed);
            seed.SupplierProfiles.Add(NewProfile(mixed.Id, "billing"));
            seed.Users.Add(NewUser("billing", u =>
            {
                u.OrgId = mixed.Id;
                u.SupplierOrgId = mixed.Id;
            }));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var user = await GetUserAsync("billing");
        Assert.Equal(mixed.Id, user.OrgId);
        Assert.NotNull(user.SupplierOrgId);
        Assert.NotEqual(mixed.Id, user.SupplierOrgId);
        await using var check = NewContext();
        var org = await check.Orgs.AsNoTracking().SingleAsync(o => o.Id == mixed.Id);
        Assert.Equal(OrgType.Host, org.OrgType);
        Assert.Equal("cus_pl05test", org.StripeCustomerId);
    }

    [PostgresFact]
    public async Task SeparateSql_HostUserAndUserWithoutOrg_AreLeftUntouched()
    {
        var hostOrg = NewOrg("host", OrgType.Host);
        await using (var seed = NewContext())
        {
            seed.Orgs.Add(hostOrg);
            seed.Users.AddRange(
                NewUser("host-user", u => u.OrgId = hostOrg.Id),
                NewUser("no-org"));
            await seed.SaveChangesAsync();
        }

        await RunMigrationSqlAsync();

        var hostUser = await GetUserAsync("host-user");
        Assert.Equal(hostOrg.Id, hostUser.OrgId);
        Assert.Null(hostUser.SupplierOrgId);
        var noOrgUser = await GetUserAsync("no-org");
        Assert.Null(noOrgUser.OrgId);
        Assert.Null(noOrgUser.SupplierOrgId);
    }

    private async Task RunMigrationSqlAsync()
    {
        await using var migrate = NewContext();
        await migrate.Database.ExecuteSqlRawAsync(SeparateSupplierOrgFromHostOrgId.SeparateSql);
    }

    private async Task<User> GetUserAsync(string key)
    {
        await using var db = NewContext();
        return await db.Users.AsNoTracking().SingleAsync(u => u.Id == $"auth0|mig-{key}");
    }

    private static Casazen.Core.Entities.Org NewOrg(string name, OrgType orgType) => new()
    {
        Name = $"Org {name}",
        Slug = $"mig-{name}-{Guid.NewGuid():N}",
        DisplayName = $"Org {name}",
        ContactEmail = $"{name}@example.com",
        OrgType = orgType,
        PlanTier = PlanTier.Starter,
        IsActive = true,
    };

    private static SupplierProfile NewProfile(Guid orgId, string key) => new()
    {
        OrgId = orgId,
        Email = $"{key}.{Guid.NewGuid():N}@example.com",
        LegalName = $"Fornitore {key} Srl",
        Phone = "+39 06 050505",
        ComuniJson = """["H501"]""",
    };

    private static Property NewProperty(Guid orgId, string ownerId) => new()
    {
        OwnerId = ownerId,
        OrgId = orgId,
        Name = "Casa PL-05",
        Address = $"Via Migrazione {Guid.NewGuid():N}",
        City = "Roma",
        PostalCode = "00100",
        Bedrooms = 1,
        Bathrooms = 1,
        MaxGuests = 2,
        NightlyRate = 80m,
        IsActive = true,
    };

    private static User NewUser(string key, Action<User>? configure = null)
    {
        var user = new User
        {
            Id = $"auth0|mig-{key}",
            Email = $"{key}.{Guid.NewGuid():N}@example.com",
            FirstName = "Migrazione",
            LastName = key,
            Role = UserRole.Supplier,
            IsActive = true,
        };
        configure?.Invoke(user);
        return user;
    }
}
