using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
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
    private static AppDbContext NewDb(string? name = null, ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(name ?? $"entitlement-{Guid.NewGuid()}")
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options, tenant);

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId => orgId;
        public bool FilterEnabled => true;
    }

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
    public async Task CreatePropertyWithinLimitAsync_AtStarterLimit_ReturnsNullWithoutCreating()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 3);
        var service = new EntitlementService(db, Config());
        var createCalls = 0;

        var created = await service.CreatePropertyWithinLimitAsync(orgId, () =>
        {
            createCalls++;
            return Task.FromResult(new Property());
        });

        Assert.Null(created);
        Assert.Equal(0, createCalls);
    }

    [Fact]
    public async Task CreatePropertyWithinLimitAsync_BelowStarterLimit_CreatesTheProperty()
    {
        await using var db = NewDb();
        var orgId = await SeedOrgWithPropertiesAsync(db, PlanTier.Starter, properties: 2);
        var service = new EntitlementService(db, Config());

        var created = await service.CreatePropertyWithinLimitAsync(orgId, async () =>
        {
            var property = new Property { OwnerId = "auth0|owner", OrgId = orgId, Name = "P3", Address = "A", City = "Rome" };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return property;
        });

        Assert.NotNull(created);
        Assert.Equal(3, await db.Properties.CountAsync(p => p.OrgId == orgId));
    }

    [Fact]
    public async Task GetEntitlementAsync_CallerOfAnotherOrg_CountsEveryPropertyOfTheOrg()
    {
        // A1-21: the admin plan change runs with the admin's tenant filter; usage must still be the org's.
        var dbName = $"entitlement-{Guid.NewGuid()}";
        Guid orgId;
        await using (var seed = NewDb(dbName))
            orgId = await SeedOrgWithPropertiesAsync(seed, PlanTier.Starter, properties: 2);
        await using var adminDb = NewDb(dbName, new FixedTenantContext(Guid.NewGuid()));
        var service = new EntitlementService(adminDb, Config());

        var result = await service.GetEntitlementAsync(orgId);

        Assert.Equal(0, await adminDb.Properties.CountAsync(p => p.OrgId == orgId)); // the filter hides them
        Assert.Equal(2, result.PropertyCount);
        Assert.True(result.CanAddProperty);
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

    [Theory]
    [InlineData(SubscriptionStatus.Incomplete)]
    [InlineData(SubscriptionStatus.Unpaid)]
    public void ResolveEffectiveTier_IncompleteOrUnpaid_ReturnsStarter(SubscriptionStatus status)
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        // A1-11: a first payment not yet succeeded or retries exhausted never grant the paid tier.
        Assert.Equal(PlanTier.Starter, service.ResolveEffectiveTier(OrgWith(PlanTier.Scale, status)));
    }

    [Fact]
    public void ResolveEffectiveTier_PastDueWithoutStartDate_FailsClosedToStarter()
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        Assert.Equal(PlanTier.Starter, service.ResolveEffectiveTier(OrgWith(PlanTier.Pro, SubscriptionStatus.PastDue, pastDueSince: null)));
    }

    [Fact]
    public void ResolveEffectiveTier_PastDueWithinGrace_ReturnsStoredTier()
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());

        Assert.Equal(PlanTier.Pro, service.ResolveEffectiveTier(OrgWith(PlanTier.Pro, SubscriptionStatus.PastDue, DateTime.UtcNow.AddDays(-1))));
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
        Assert.Null(await service.CreatePropertyWithinLimitAsync(org.Id, () => Task.FromResult(new Property())));
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
