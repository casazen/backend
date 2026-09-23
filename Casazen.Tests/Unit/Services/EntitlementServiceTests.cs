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

    /// <summary>A paid tier is seeded with the active subscription that pays for it (#274).</summary>
    private static async Task<Guid> SeedOrgWithPropertiesAsync(AppDbContext db, PlanTier tier, int properties)
    {
        var paid = tier != PlanTier.Starter;
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = $"org-{Guid.NewGuid():N}",
            DisplayName = "Org",
            ContactEmail = "o@x.it",
            PlanTier = tier,
            SubscriptionId = paid ? $"sub_{Guid.NewGuid():N}" : null,
            SubscriptionStatus = paid ? SubscriptionStatus.Active : SubscriptionStatus.None,
            IsActive = true,
        };
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

    // ── Effective tier (#274, A3-07, A9-02, A3-37) ───────────────────────────────

    private static OrgEntity OrgWith(PlanTier tier, SubscriptionStatus status, DateTime? pastDueSince = null) => new()
    {
        Name = "Org",
        Slug = $"org-{Guid.NewGuid():N}",
        DisplayName = "Org",
        ContactEmail = "o@x.it",
        PlanTier = tier,
        SubscriptionId = status == SubscriptionStatus.None ? null : $"sub_{Guid.NewGuid():N}",
        SubscriptionStatus = status,
        PastDueSince = pastDueSince,
        IsActive = true,
    };

    [Theory]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Scale)]
    public void ResolveEffectiveTier_PaidTierWithoutSubscription_ReturnsStarter(PlanTier stored)
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        Assert.Equal(PlanTier.Starter, service.ResolveEffectiveTier(OrgWith(stored, SubscriptionStatus.None)));
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trialing)]
    public void ResolveEffectiveTier_PaidSubscription_ReturnsStoredTier(SubscriptionStatus status)
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        Assert.Equal(PlanTier.Scale, service.ResolveEffectiveTier(OrgWith(PlanTier.Scale, status)));
    }

    [Fact]
    public void ResolveEffectiveTier_Canceled_ReturnsStarter()
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        Assert.Equal(PlanTier.Starter, service.ResolveEffectiveTier(OrgWith(PlanTier.Pro, SubscriptionStatus.Canceled)));
    }

    [Fact]
    public void ResolveEffectiveTier_UnmappedStatus_FailsClosedToStarter()
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        Assert.Equal(PlanTier.Starter, service.ResolveEffectiveTier(OrgWith(PlanTier.Pro, (SubscriptionStatus)99)));
    }

    [Fact]
    public async Task GetEntitlementAsync_StoredProWithoutSubscription_ReturnsStarterLimits()
    {
        await using var db = NewDb();
        var org = OrgWith(PlanTier.Pro, SubscriptionStatus.None);
        db.Orgs.Add(org);
        for (var i = 0; i < 3; i++)
            db.Properties.Add(new Property { OwnerId = "auth0|owner", OrgId = org.Id, Name = $"P{i}", Address = "A", City = "Rome" });
        await db.SaveChangesAsync();
        var service = new EntitlementService(db, Config());

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal(PlanTier.Starter.ToString(), result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
        Assert.False(result.CanAddProperty);
        Assert.False(await service.ReservePropertySlotAsync(org.Id));
    }

    [Fact]
    public async Task CanUseCustomDomainAsync_StoredProWithoutSubscription_ReturnsFalse()
    {
        await using var db = NewDb();
        var org = OrgWith(PlanTier.Pro, SubscriptionStatus.None);
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        var service = new EntitlementService(db, Config());

        Assert.False(await service.CanUseCustomDomainAsync(org.Id));
    }

    [Fact]
    public async Task CanUseCustomDomainAsync_ProWithActiveSubscription_ReturnsTrue()
    {
        await using var db = NewDb();
        var org = OrgWith(PlanTier.Pro, SubscriptionStatus.Active);
        db.Orgs.Add(org);
        await db.SaveChangesAsync();
        var service = new EntitlementService(db, Config());

        Assert.True(await service.CanUseCustomDomainAsync(org.Id));
    }
}
