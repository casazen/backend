using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AC8 â€” plan entitlement. Verifies the tierâ†’limit map, the boundary at which another
/// property may/may not be created, configuration overrides, and the Starter fallback.
/// </summary>
public class EntitlementServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"entitlement-{Guid.NewGuid()}")
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IConfiguration Config(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();

    private static async Task<Guid> SeedOrgWithPropertiesAsync(AppDbContext db, PlanTier tier, int properties)
    {
        var org = new OrgEntity { Name = "Org", Slug = $"org-{Guid.NewGuid():N}", DisplayName = "Org", ContactEmail = "o@x.it", PlanTier = tier, IsActive = true };
        db.Orgs.Add(org);
        for (var i = 0; i < properties; i++)
            db.Properties.Add(new Property { OwnerId = "auth0|owner", OrgId = org.Id, Name = $"P{i}", Address = "A", City = "Rome" });
        await db.SaveChangesAsync();
        return org.Id;
    }

    [Theory]
    [InlineData(PlanTier.Starter, 3)]
    [InlineData(PlanTier.Pro, 50)]
    public async Task GetEntitlementAsync_ReturnsTierLimitFromDefaultMap(PlanTier tier, int expectedMax)
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, tier, properties: 1);
        var service = new EntitlementService(db, Config());

        var result = await service.GetEntitlementAsync(orgId);

        Assert.Equal(tier.ToString(), result.PlanTier);
        Assert.Equal(expectedMax, result.MaxProperties);
        Assert.Equal(1, result.PropertyCount);
        Assert.True(result.CanAddProperty);
    }

    [Fact]
    public async Task CanAddPropertyAsync_AtStarterLimit_ReturnsFalse()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 3);
        var service = new EntitlementService(db, Config());

        Assert.False(await service.CanAddPropertyAsync(orgId));
        Assert.False((await service.GetEntitlementAsync(orgId)).CanAddProperty);
    }

    [Fact]
    public async Task CanAddPropertyAsync_BelowStarterLimit_ReturnsTrue()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 2);
        var service = new EntitlementService(db, Config());

        Assert.True(await service.CanAddPropertyAsync(orgId));
    }

    [Fact]
    public async Task GetEntitlementAsync_HonorsConfigurationOverride()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 1);
        var service = new EntitlementService(db, Config(new()
        {
            ["Entitlement:Tiers:Starter:MaxProperties"] = "1",
        }));

        var result = await service.GetEntitlementAsync(orgId);

        Assert.Equal(1, result.MaxProperties);
        Assert.False(result.CanAddProperty); // 1 used, limit 1
    }

    [Fact]
    public async Task ReservePropertySlotAsync_AtStarterLimit_ReturnsFalse()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 3);
        var service = new EntitlementService(db, Config());

        Assert.False(await service.ReservePropertySlotAsync(orgId));
    }

    [Fact]
    public async Task ReservePropertySlotAsync_BelowStarterLimit_ReturnsTrue()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 2);
        var service = new EntitlementService(db, Config());

        Assert.True(await service.ReservePropertySlotAsync(orgId));
    }

    [Fact]
    public async Task GetEntitlementAsync_UnknownOrg_FallsBackToStarter()
    {
        await using var db = NewDb();
        var service = new EntitlementService(db, Config());

        var result = await service.GetEntitlementAsync(Guid.NewGuid());

        Assert.Equal(PlanTier.Starter.ToString(), result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
        Assert.Equal(0, result.PropertyCount);
    }

    [Fact]
    public async Task GetEntitlementAsync_PastDueWithinGrace_KeepsPaidTierLimits()
    {
        await using var db = NewDb();
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Org",
            ContactEmail = "o@x.it",
            PlanTier = PlanTier.Pro,
            SubscriptionStatus = SubscriptionStatus.PastDue,
            PastDueSince = DateTime.UtcNow.AddDays(-2),
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();

        var service = new EntitlementService(db, Config(new()
        {
            ["Billing:PastDueGraceDays"] = "7",
        }));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal(PlanTier.Pro.ToString(), result.PlanTier);
        Assert.Equal(50, result.MaxProperties);
    }

    [Fact]
    public async Task GetEntitlementAsync_PastDueBeyondGrace_DowngradesToStarter()
    {
        await using var db = NewDb();
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Org",
            ContactEmail = "o@x.it",
            PlanTier = PlanTier.Pro,
            SubscriptionStatus = SubscriptionStatus.PastDue,
            PastDueSince = DateTime.UtcNow.AddDays(-10),
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();

        var service = new EntitlementService(db, Config(new()
        {
            ["Billing:PastDueGraceDays"] = "7",
        }));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal(PlanTier.Starter.ToString(), result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
    }

    [Fact]
    public async Task SyncFromSubscriptionAsync_PastDueBeyondGrace_PersistsStarterTier()
    {
        await using var db = NewDb();
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Org",
            ContactEmail = "o@x.it",
            PlanTier = PlanTier.Pro,
            SubscriptionStatus = SubscriptionStatus.PastDue,
            PastDueSince = DateTime.UtcNow.AddDays(-10),
            IsActive = true,
        };
        db.Orgs.Add(org);
        await db.SaveChangesAsync();

        var service = new EntitlementService(db, Config(new()
        {
            ["Billing:PastDueGraceDays"] = "7",
        }));

        await service.SyncFromSubscriptionAsync(org.Id);

        var updated = await db.Orgs.FindAsync(org.Id);
        Assert.NotNull(updated);
        Assert.Equal(PlanTier.Starter, updated!.PlanTier);
    }
}
