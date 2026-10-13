using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Migrations;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// PL-02 data migration (<see cref="AssignNoneRoleToUsersWithoutOnboarding"/>) on real PostgreSQL: only the users with no
/// trace of use move from the old default <c>PropertyOwner</c> to <c>None</c>; everybody else keeps the stored role.
/// </summary>
public class AssignNoneRoleMigrationPostgresTests : IAsyncLifetime
{
    private static readonly DateTime OnboardedAt = new(2026, 7, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ConsentRecordedAt = new(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);

    private PostgresTestDatabase? _database;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
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
    public async Task BackfillSql_ExistingUsers_OnlyNeverActivatedOwnersBecomeNone()
    {
        await using (var seed = NewContext())
        {
            var hostOrg = NewOrg("host");
            var legacyOrg = NewOrg("legacy");
            var supplierOrg = NewOrg("supplier");
            var otherOrg = NewOrg("other");
            seed.Orgs.AddRange(hostOrg, legacyOrg, supplierOrg, otherOrg);

            seed.Users.AddRange(
                NewUser("never-activated", UserRole.PropertyOwner),
                NewUser("onboarded", UserRole.PropertyOwner, u =>
                {
                    u.OrgId = hostOrg.Id;
                    u.RentalType = RentalType.ShortTerm;
                    u.OnboardingCompletedAt = OnboardedAt;
                }),
                NewUser("rental-type-with-consents", UserRole.PropertyOwner, u =>
                {
                    u.OrgId = otherOrg.Id;
                    u.RentalType = RentalType.Both;
                }),
                NewUser("legacy-org", UserRole.PropertyOwner, u => u.OrgId = legacyOrg.Id),
                NewUser("rental-type-only", UserRole.PropertyOwner, u => u.RentalType = RentalType.LongTerm),
                NewUser("supplier", UserRole.PropertyOwner, u => u.SupplierOrgId = supplierOrg.Id),
                NewUser("consents-only", UserRole.PropertyOwner),
                NewUser("owns-property", UserRole.PropertyOwner),
                NewUser("admin", UserRole.Admin),
                NewUser("landlord", UserRole.LongTermLandlord),
                NewUser("manager", UserRole.PropertyManager),
                NewUser("staff", UserRole.Staff));
            seed.ConsentRecords.AddRange(
                new ConsentRecord
                {
                    UserId = "auth0|mig-consents-only",
                    OrgId = otherOrg.Id,
                    Type = ConsentType.Tos,
                    Version = "2026-06-v1",
                },
                new ConsentRecord
                {
                    UserId = "auth0|mig-rental-type-with-consents",
                    OrgId = otherOrg.Id,
                    Type = ConsentType.Dpa,
                    Version = "2026-06-v1",
                    RecordedAt = ConsentRecordedAt,
                });
            seed.Properties.Add(new Property
            {
                OwnerId = "auth0|mig-owns-property",
                OrgId = otherOrg.Id,
                Name = "Casa migrazione",
                Address = $"Via Migrazione {Guid.NewGuid():N}",
                City = "Roma",
            });
            await seed.SaveChangesAsync();
        }

        await using (var migrate = NewContext())
            await migrate.Database.ExecuteSqlRawAsync(AssignNoneRoleToUsersWithoutOnboarding.BackfillSql);

        await using var db = NewContext();
        var roles = await db.Users.AsNoTracking()
            .Where(u => u.Id.StartsWith("auth0|mig-"))
            .ToDictionaryAsync(u => u.Id["auth0|mig-".Length..], u => u.Role);

        var onboardedAt = await db.Users.AsNoTracking()
            .Where(u => u.Id.StartsWith("auth0|mig-"))
            .ToDictionaryAsync(u => u.Id["auth0|mig-".Length..], u => u.OnboardingCompletedAt);

        Assert.Equal(UserRole.None, roles["never-activated"]);
        Assert.Null(onboardedAt["never-activated"]);
        // Onboarded before the timestamp existed (rental type written by the onboarding): backfilled, role kept.
        Assert.NotNull(onboardedAt["rental-type-only"]);
        Assert.Equal(ConsentRecordedAt, onboardedAt["rental-type-with-consents"]!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(UserRole.PropertyOwner, roles["rental-type-with-consents"]);
        // Existing timestamps are never rewritten; no rental type, no timestamp.
        Assert.Equal(OnboardedAt, onboardedAt["onboarded"]!.Value, TimeSpan.FromSeconds(1));
        Assert.Null(onboardedAt["legacy-org"]);
        Assert.Equal(UserRole.PropertyOwner, roles["onboarded"]);
        Assert.Equal(UserRole.PropertyOwner, roles["legacy-org"]);
        Assert.Equal(UserRole.PropertyOwner, roles["rental-type-only"]);
        Assert.Equal(UserRole.PropertyOwner, roles["supplier"]);
        Assert.Equal(UserRole.PropertyOwner, roles["consents-only"]);
        Assert.Equal(UserRole.PropertyOwner, roles["owns-property"]);
        Assert.Equal(UserRole.Admin, roles["admin"]);
        Assert.Equal(UserRole.LongTermLandlord, roles["landlord"]);
        Assert.Equal(UserRole.PropertyManager, roles["manager"]);
        Assert.Equal(UserRole.Staff, roles["staff"]);
    }

    [PostgresFact]
    public async Task RevertSql_NoneUsers_GoBackToThePreviousDefault()
    {
        await using (var seed = NewContext())
        {
            seed.Users.AddRange(NewUser("revert-none", UserRole.None), NewUser("revert-admin", UserRole.Admin));
            await seed.SaveChangesAsync();
        }

        await using (var migrate = NewContext())
            await migrate.Database.ExecuteSqlRawAsync(AssignNoneRoleToUsersWithoutOnboarding.RevertSql);

        await using var db = NewContext();
        Assert.Equal(UserRole.PropertyOwner, await db.Users.Where(u => u.Id == "auth0|mig-revert-none").Select(u => u.Role).SingleAsync());
        Assert.Equal(UserRole.Admin, await db.Users.Where(u => u.Id == "auth0|mig-revert-admin").Select(u => u.Role).SingleAsync());
    }

    private static Casazen.Core.Entities.Org NewOrg(string name) => new()
    {
        Name = $"Org {name}",
        Slug = $"mig-{name}-{Guid.NewGuid():N}",
        DisplayName = $"Org {name}",
    };

    private static User NewUser(string key, UserRole role, Action<User>? configure = null)
    {
        var user = new User
        {
            Id = $"auth0|mig-{key}",
            Email = $"{key}.{Guid.NewGuid():N}@example.com",
            FirstName = "Migrazione",
            LastName = key,
            Role = role,
            IsActive = true,
        };
        configure?.Invoke(user);
        return user;
    }
}
