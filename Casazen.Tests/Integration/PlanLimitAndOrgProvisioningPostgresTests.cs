using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Service-level races of TN-4 on real PostgreSQL, each "request" on its own <see cref="AppDbContext"/>:
/// the plan-limit check and the insert are atomic (A1-21), org provisioning of one user creates one org and
/// never unlinks it (A1-14).
/// </summary>
public class PlanLimitAndOrgProvisioningPostgresTests : IAsyncLifetime
{
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

    private static EntitlementService NewEntitlementService(AppDbContext db) =>
        new(db, new ConfigurationBuilder().Build());

    [PostgresFact]
    public async Task CreatePropertyWithinLimitAsync_SecondCreateStartsWhileFirstInserts_WaitsAndIsRefused()
    {
        var orgId = await SeedOrgWithPropertiesAsync(properties: 2); // Starter: one slot left
        var firstInsideSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var firstDb = NewContext();
        await using var secondDb = NewContext();
        var first = NewEntitlementService(firstDb).CreatePropertyWithinLimitAsync(orgId, async () =>
        {
            firstInsideSlot.SetResult();
            // Leave the second request all the time it needs to count while this one has not inserted yet.
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            return await InsertPropertyAsync(firstDb, orgId, "Via Primo 1");
        });

        await firstInsideSlot.Task;
        var second = NewEntitlementService(secondDb).CreatePropertyWithinLimitAsync(
            orgId, () => InsertPropertyAsync(secondDb, orgId, "Via Secondo 2"));

        Assert.NotNull(await first);
        Assert.Null(await second);
        await using var check = NewContext();
        Assert.Equal(3, await check.Properties.CountAsync(p => p.OrgId == orgId));
    }

    [PostgresFact]
    public async Task CreatePropertyWithinLimitAsync_CreateFails_RollsBackAndKeepsTheSlot()
    {
        var orgId = await SeedOrgWithPropertiesAsync(properties: 2);

        await using (var failing = NewContext())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewEntitlementService(failing).CreatePropertyWithinLimitAsync(orgId, async () =>
                {
                    await InsertPropertyAsync(failing, orgId, "Via Annullata 1");
                    throw new InvalidOperationException("create failed after the insert");
                }));
        }

        await using var retry = NewContext();
        Assert.Equal(2, await retry.Properties.CountAsync(p => p.OrgId == orgId));
        Assert.NotNull(await NewEntitlementService(retry).CreatePropertyWithinLimitAsync(
            orgId, () => InsertPropertyAsync(retry, orgId, "Via Riprovata 1")));
    }

    [PostgresFact]
    public async Task EnsureOrgForUserAsync_ParallelFirstAccesses_LinkOneOrg()
    {
        var userId = await SeedUserAsync();

        var contexts = Enumerable.Range(0, 6).Select(_ => NewContext()).ToList();
        try
        {
            var orgs = await Task.WhenAll(contexts.Select(db =>
                new OrgService(db).EnsureOrgForUserAsync(userId, "first@example.com", "Prima Volta")));

            Assert.Single(orgs.Select(o => o.Id).Distinct());
            await using var check = NewContext();
            var user = await check.Users.SingleAsync(u => u.Id == userId);
            Assert.Equal(orgs[0].Id, user.OrgId);
            Assert.Equal(1, await check.Orgs.CountAsync(o => o.Slug.StartsWith($"org-{userId.Replace("|", "-")}")));
        }
        finally
        {
            foreach (var db in contexts)
                await db.DisposeAsync();
        }
    }

    [PostgresFact]
    public async Task EnsureOrgForUserAsync_UserLoadedBeforeAParallelProvisioning_ReturnsThatOrgAndKeepsTheLink()
    {
        var userId = await SeedUserAsync();

        // Request A loads the user (no org yet); request B provisions the org in the meantime.
        await using var requestA = NewContext();
        var repositoryA = new UserRepository(requestA);
        var userA = await repositoryA.GetBySubAsync(userId);
        Assert.Null(userA!.OrgId);

        OrgEntity orgB;
        await using (var requestB = NewContext())
            orgB = await new OrgService(requestB).EnsureOrgForUserAsync(userId, "first@example.com", "Prima Volta");

        var orgA = await new OrgService(requestA).EnsureOrgForUserAsync(userId, "first@example.com", "Prima Volta");
        userA.FirstName = "Aggiornato";
        await repositoryA.UpdateAsync(userA);

        Assert.Equal(orgB.Id, orgA.Id);
        Assert.Equal(orgB.Id, userA.OrgId);
        await using var check = NewContext();
        var stored = await check.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(orgB.Id, stored.OrgId);
        Assert.Equal("Aggiornato", stored.FirstName);
        Assert.Equal(1, await check.Orgs.CountAsync(o => o.Slug.StartsWith($"org-{userId.Replace("|", "-")}")));
    }

    [PostgresFact]
    public async Task AddIfAbsentAsync_UserInsertedByAParallelRequest_ReturnsTheStoredRow()
    {
        var userId = await SeedUserAsync();

        await using var db = NewContext();
        var (user, created) = await new UserRepository(db).AddIfAbsentAsync(new User
        {
            Id = userId,
            Email = "second@example.com",
            FirstName = "Seconda",
            LastName = "Richiesta",
        });

        Assert.False(created);
        Assert.Equal("first@example.com", user.Email);
        Assert.Equal(1, await db.Users.CountAsync(u => u.Id == userId));
    }

    private async Task<string> SeedUserAsync()
    {
        var userId = $"auth0|tn4-{Guid.NewGuid():N}";
        await using var db = NewContext();
        db.Users.Add(new User
        {
            Id = userId,
            Email = "first@example.com",
            FirstName = "Prima",
            LastName = "Volta",
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<Guid> SeedOrgWithPropertiesAsync(int properties)
    {
        await using var db = NewContext();
        var org = new OrgEntity
        {
            Name = "Org limite",
            Slug = $"org-limit-{Guid.NewGuid():N}",
            DisplayName = "Org limite",
            ContactEmail = "limit@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        for (var i = 0; i < properties; i++)
            await InsertPropertyAsync(db, org.Id, $"Via Esistente {i}");
        return org.Id;
    }

    private static async Task<Property> InsertPropertyAsync(AppDbContext db, Guid orgId, string address)
    {
        var property = new Property
        {
            OwnerId = "auth0|limit-owner",
            OrgId = orgId,
            Name = address,
            Description = "TN-4",
            Address = address,
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 2,
            NightlyRate = 80m,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }
}
