using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// BL-01: "accesso aperto" in <see cref="EntitlementService"/>. With <c>Entitlement:OpenAccess</c> off (the default) nothing
/// changes; with it on, the effective tier of an org (limits, custom domain, "Realizzato con") is at least the configured one
/// and never lower than the one it pays for, while the stored tier and the Stripe data stay untouched and the plan change
/// rules keep comparing with the paid tier.
/// </summary>
public class EntitlementServiceOpenAccessTests
{
    private static readonly Dictionary<string, string?> ScaleOpen = new()
    {
        ["Entitlement:OpenAccess:Enabled"] = "true",
        ["Entitlement:OpenAccess:Tier"] = "Scale",
    };

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"entitlement-open-access-{Guid.NewGuid()}")
            .ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IConfiguration Config(Dictionary<string, string?>? values = null, params (string Key, string? Value)[] more)
    {
        var all = new Dictionary<string, string?>(values ?? []);
        foreach (var (key, value) in more)
            all[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(all).Build();
    }

    private static IConfiguration OpenAccessOn(string tier) =>
        Config(null, ("Entitlement:OpenAccess:Enabled", "true"), ("Entitlement:OpenAccess:Tier", tier));

    private static OrgEntity OrgWith(PlanTier stored, SubscriptionStatus status, DateTime? pastDueSince = null) => new()
    {
        Name = "Org",
        Slug = $"org-{Guid.NewGuid():N}",
        DisplayName = "Org",
        ContactEmail = "o@x.it",
        PlanTier = stored,
        SubscriptionId = status == SubscriptionStatus.None ? null : $"sub_{Guid.NewGuid():N}",
        SubscriptionStatus = status,
        PastDueSince = pastDueSince,
        IsActive = true,
    };

    private static async Task<OrgEntity> SeedAsync(AppDbContext db, OrgEntity org, int properties = 0)
    {
        db.Orgs.Add(org);
        for (var i = 0; i < properties; i++)
            db.Properties.Add(new Property { OwnerId = "auth0|owner", OrgId = org.Id, Name = $"P{i}", Address = "A", City = "Rome" });
        await db.SaveChangesAsync();
        return org;
    }

    private static async Task AssertStoredPlanUntouchedAsync(AppDbContext db, OrgEntity before)
    {
        db.ChangeTracker.Clear();
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == before.Id);
        Assert.Equal(before.PlanTier, stored.PlanTier);
        Assert.Equal(before.SubscriptionStatus, stored.SubscriptionStatus);
        Assert.Equal(before.SubscriptionId, stored.SubscriptionId);
        Assert.Equal(before.PastDueSince, stored.PastDueSince);
        Assert.Equal(before.CurrentPeriodEnd, stored.CurrentPeriodEnd);
    }

    // ─── Off (the default): nothing changes ──────────────────────────────────────

    [Fact]
    public async Task GetEntitlementAsync_DefaultConfiguration_OrgWithoutSubscriptionIsStarter()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);
        var service = new EntitlementService(db, Config());

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
        Assert.False(result.CanAddProperty);
        Assert.False(result.OpenAccess);
    }

    [Theory]
    [InlineData("false", "Scale")]
    [InlineData("", "Scale")]
    [InlineData(null, "Scale")]
    [InlineData("false", "Pro")]
    public async Task GetEntitlementAsync_SwitchOff_IgnoresTheTier(string? enabled, string tier)
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);
        var service = new EntitlementService(db, Config(null,
            ("Entitlement:OpenAccess:Enabled", enabled), ("Entitlement:OpenAccess:Tier", tier)));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
        Assert.False(result.OpenAccess);
        Assert.False(await service.CanUseCustomDomainAsync(org.Id));
        Assert.Equal(PlanTier.Starter, service.ResolveEffectiveTier(org));
    }

    [Theory]
    [InlineData(PlanTier.Starter, SubscriptionStatus.None, PlanTier.Starter)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.None, PlanTier.Starter)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Active, PlanTier.Pro)]
    [InlineData(PlanTier.Scale, SubscriptionStatus.Trialing, PlanTier.Scale)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Canceled, PlanTier.Starter)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Unpaid, PlanTier.Starter)]
    [InlineData(PlanTier.Scale, SubscriptionStatus.Incomplete, PlanTier.Starter)]
    public void ResolveEffectiveTier_SwitchOff_IsExactlyThePaidTier(PlanTier stored, SubscriptionStatus status, PlanTier expected)
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config());
        var org = OrgWith(stored, status);

        Assert.Equal(expected, service.ResolveEffectiveTier(org));
        Assert.Equal(expected, service.ResolvePaidTier(org));
    }

    // ─── On: an org without a subscription gets the tier of the open access ─────────

    [Fact]
    public async Task GetEntitlementAsync_ScaleOpen_OrgWithoutSubscriptionGetsTheScaleLimits()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 5);
        var service = new EntitlementService(db, Config(ScaleOpen));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier);
        Assert.Equal(int.MaxValue, result.MaxProperties);
        Assert.Equal(5, result.PropertyCount);
        Assert.True(result.CanAddProperty);
        Assert.True(result.OpenAccess);
    }

    [Fact]
    public async Task GetEntitlementAsync_ScaleOpenWithoutTier_DefaultsToScale()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None));
        var service = new EntitlementService(db, Config(null, ("Entitlement:OpenAccess:Enabled", "true")));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier);
        Assert.True(result.OpenAccess);
    }

    [Fact]
    public async Task GetEntitlementAsync_ProOpen_OrgWithoutSubscriptionGetsTheProLimits()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 4);
        var service = new EntitlementService(db, OpenAccessOn("Pro"));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Pro", result.PlanTier);
        Assert.Equal(50, result.MaxProperties);
        Assert.True(result.CanAddProperty);
        Assert.True(result.OpenAccess);
    }

    [Fact]
    public async Task GetEntitlementAsync_ProOpenWithTheLimitConfigured_UsesTheLimitOfThatTier()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 2);
        var service = new EntitlementService(db, Config(null,
            ("Entitlement:OpenAccess:Enabled", "true"),
            ("Entitlement:OpenAccess:Tier", "Pro"),
            ("Entitlement:Tiers:Pro:MaxProperties", "2")));

        var result = await service.GetEntitlementAsync(org.Id);

        // D35: the limits stay per tier and follow the forced one, including their configuration overrides.
        Assert.Equal("Pro", result.PlanTier);
        Assert.Equal(2, result.MaxProperties);
        Assert.False(result.CanAddProperty);
    }

    [Fact]
    public async Task GetEntitlementAsync_StarterOpen_ChangesNothing()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);
        var service = new EntitlementService(db, OpenAccessOn("Starter"));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
        Assert.False(result.CanAddProperty);
        Assert.False(result.OpenAccess); // on, but it does not open anything for this org
    }

    [Fact]
    public async Task CanAddPropertyAsync_ScaleOpen_AboveTheStarterLimit_ReturnsTrue()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);

        Assert.False(await new EntitlementService(db, Config()).CanAddPropertyAsync(org.Id));
        Assert.True(await new EntitlementService(db, Config(ScaleOpen)).CanAddPropertyAsync(org.Id));
    }

    [Fact]
    public async Task CreatePropertyWithinLimitAsync_ScaleOpen_CreatesBeyondTheStarterLimit()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);
        var service = new EntitlementService(db, Config(ScaleOpen));

        var created = await service.CreatePropertyWithinLimitAsync(org.Id, async () =>
        {
            var property = new Property { OwnerId = "auth0|owner", OrgId = org.Id, Name = "P4", Address = "A", City = "Rome" };
            db.Properties.Add(property);
            await db.SaveChangesAsync();
            return property;
        });

        Assert.NotNull(created);
        Assert.Equal(4, await db.Properties.CountAsync(p => p.OrgId == org.Id));
    }

    [Fact]
    public async Task CreatePropertyWithinLimitAsync_ProOpen_StopsAtTheProLimit()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 2);
        var service = new EntitlementService(db, Config(null,
            ("Entitlement:OpenAccess:Enabled", "true"),
            ("Entitlement:OpenAccess:Tier", "Pro"),
            ("Entitlement:Tiers:Pro:MaxProperties", "2")));
        var createCalls = 0;

        var created = await service.CreatePropertyWithinLimitAsync(org.Id, () =>
        {
            createCalls++;
            return Task.FromResult(new Property());
        });

        Assert.Null(created);
        Assert.Equal(0, createCalls);
    }

    [Fact]
    public async Task CanUseCustomDomainAsync_ScaleOpen_OrgWithoutSubscription_ReturnsTrue()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None));

        Assert.False(await new EntitlementService(db, Config()).CanUseCustomDomainAsync(org.Id));
        Assert.True(await new EntitlementService(db, Config(ScaleOpen)).CanUseCustomDomainAsync(org.Id));
        Assert.False(await new EntitlementService(db, OpenAccessOn("Starter")).CanUseCustomDomainAsync(org.Id));
        Assert.True(await new EntitlementService(db, OpenAccessOn("Pro")).CanUseCustomDomainAsync(org.Id));
    }

    [Theory]
    [InlineData(PlanTier.Starter)]
    [InlineData(PlanTier.Pro)]
    [InlineData(PlanTier.Scale)]
    public void ResolveEffectiveTier_ScaleOpen_OrgWithoutSubscription_IsScale(PlanTier stored)
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config(ScaleOpen));

        // "Realizzato con" (Starter only), the custom domain and the seats all read this tier.
        Assert.Equal(PlanTier.Scale, service.ResolveEffectiveTier(OrgWith(stored, SubscriptionStatus.None)));
    }

    // ─── On: never worse than what the org pays for ─────────────────────────────

    [Theory]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Active)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Trialing)]
    public async Task GetEntitlementAsync_PaidProWithStarterOpen_StaysPro(PlanTier stored, SubscriptionStatus status)
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(stored, status), properties: 10);
        var service = new EntitlementService(db, OpenAccessOn("Starter"));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Pro", result.PlanTier);
        Assert.Equal(50, result.MaxProperties);
        Assert.False(result.OpenAccess);
        Assert.Equal(PlanTier.Pro, service.ResolveEffectiveTier(org));
    }

    [Fact]
    public async Task GetEntitlementAsync_PaidScaleWithProOpen_StaysScale()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Scale, SubscriptionStatus.Active));
        var service = new EntitlementService(db, OpenAccessOn("Pro"));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier);
        Assert.Equal(int.MaxValue, result.MaxProperties);
        Assert.False(result.OpenAccess);
    }

    [Fact]
    public async Task GetEntitlementAsync_PaidScaleWithScaleOpen_ChangesNothingForThatOrg()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Scale, SubscriptionStatus.Active));
        var service = new EntitlementService(db, Config(ScaleOpen));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier);
        Assert.False(result.OpenAccess); // the org already has the tier: nothing to show as "open"
    }

    [Fact]
    public async Task GetEntitlementAsync_PaidProWithScaleOpen_IsRaisedToScale()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, SubscriptionStatus.Active), properties: 60);
        var service = new EntitlementService(db, Config(ScaleOpen));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier);
        Assert.True(result.CanAddProperty);
        Assert.True(result.OpenAccess);
        await AssertStoredPlanUntouchedAsync(db, org);
    }

    // ─── On: orgs in trouble with the payment (PastDue, unpaid, canceled, incomplete) ──

    [Fact]
    public async Task GetEntitlementAsync_PastDueWithinGrace_PaidScaleWithProOpen_StaysScale()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Scale, SubscriptionStatus.PastDue, DateTime.UtcNow.AddDays(-2)));
        var service = new EntitlementService(db, Config(null,
            ("Entitlement:OpenAccess:Enabled", "true"), ("Entitlement:OpenAccess:Tier", "Pro"), ("Billing:PastDueGraceDays", "7")));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier); // paid within the grace period, higher than the open access
        Assert.False(result.OpenAccess);
    }

    [Fact]
    public async Task GetEntitlementAsync_PastDueBeyondGrace_SwitchOff_FallsToStarter_SwitchOn_IsTheOpenAccessTier()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, SubscriptionStatus.PastDue, DateTime.UtcNow.AddDays(-10)));

        var off = await new EntitlementService(db, Config(null, ("Billing:PastDueGraceDays", "7"))).GetEntitlementAsync(org.Id);
        var on = await new EntitlementService(db, Config(ScaleOpen, ("Billing:PastDueGraceDays", "7"))).GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", off.PlanTier);
        Assert.False(off.OpenAccess);
        Assert.Equal("Scale", on.PlanTier);
        Assert.True(on.OpenAccess);
        await AssertStoredPlanUntouchedAsync(db, org);
    }

    [Fact]
    public async Task GetEntitlementAsync_PastDueWithoutStartDate_SwitchOn_IsTheOpenAccessTier()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, SubscriptionStatus.PastDue, pastDueSince: null));

        var result = await new EntitlementService(db, Config(ScaleOpen)).GetEntitlementAsync(org.Id);

        Assert.Equal("Scale", result.PlanTier);
    }

    [Theory]
    [InlineData(SubscriptionStatus.None)]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Incomplete)]
    [InlineData(SubscriptionStatus.Unpaid)]
    [InlineData((SubscriptionStatus)99)]
    public async Task GetEntitlementAsync_SubscriptionWithoutPaidAccess_SwitchOff_Starter_SwitchOn_Scale(SubscriptionStatus status)
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, status));

        var off = await new EntitlementService(db, Config()).GetEntitlementAsync(org.Id);
        var on = await new EntitlementService(db, Config(ScaleOpen)).GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", off.PlanTier);
        Assert.Equal("Scale", on.PlanTier);
        Assert.True(on.OpenAccess);
        await AssertStoredPlanUntouchedAsync(db, org);
    }

    [Fact]
    public async Task GetEntitlementAsync_UnpaidAndStarterOpen_StaysStarter()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, SubscriptionStatus.Unpaid));

        var result = await new EntitlementService(db, OpenAccessOn("Starter")).GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", result.PlanTier);
        Assert.False(result.OpenAccess);
    }

    // ─── Orgs that do not exist ──────────────────────────────────────────────────

    [Fact]
    public async Task GetEntitlementAsync_UnknownOrg_ScaleOpen_KeepsTheFailClosedStarterFallback()
    {
        await using var db = NewDb();
        var service = new EntitlementService(db, Config(ScaleOpen));

        var result = await service.GetEntitlementAsync(Guid.NewGuid());

        Assert.Equal("Starter", result.PlanTier);
        Assert.Equal(3, result.MaxProperties);
        Assert.False(result.OpenAccess);
    }

    [Fact]
    public async Task CanUseCustomDomainAsync_UnknownOrg_ScaleOpen_ReturnsFalse()
    {
        await using var db = NewDb();
        var service = new EntitlementService(db, Config(ScaleOpen));

        Assert.False(await service.CanUseCustomDomainAsync(Guid.NewGuid()));
    }

    // ─── The paid tier: what the subscription pays for, and what is stored ────────────

    [Theory]
    [InlineData(PlanTier.Pro, SubscriptionStatus.None, PlanTier.Starter)]
    [InlineData(PlanTier.Scale, SubscriptionStatus.Canceled, PlanTier.Starter)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Active, PlanTier.Pro)]
    [InlineData(PlanTier.Starter, SubscriptionStatus.Active, PlanTier.Starter)]
    public void ResolvePaidTier_ScaleOpen_IgnoresTheOpenAccess(PlanTier stored, SubscriptionStatus status, PlanTier expected)
    {
        using var db = NewDb();
        var service = new EntitlementService(db, Config(ScaleOpen));

        Assert.Equal(expected, service.ResolvePaidTier(OrgWith(stored, status)));
    }

    [Fact]
    public async Task SyncFromSubscriptionAsync_ScaleOpen_CanceledSubscription_StoresStarterNeverScale()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, SubscriptionStatus.Canceled));
        var service = new EntitlementService(db, Config(ScaleOpen));

        await service.SyncFromSubscriptionAsync(org.Id);

        // The sync downgrades like it always did; the open access is an override on read and never reaches the stored tier.
        db.ChangeTracker.Clear();
        var stored = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id);
        Assert.Equal(PlanTier.Starter, stored.PlanTier);
        Assert.Equal(SubscriptionStatus.Canceled, stored.SubscriptionStatus);
    }

    [Fact]
    public async Task SyncFromSubscriptionAsync_ScaleOpen_PastDueBeyondGrace_StoresStarterNeverScale()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Pro, SubscriptionStatus.PastDue, DateTime.UtcNow.AddDays(-10)));
        var service = new EntitlementService(db, Config(ScaleOpen, ("Billing:PastDueGraceDays", "7")));

        await service.SyncFromSubscriptionAsync(org.Id);

        db.ChangeTracker.Clear();
        Assert.Equal(PlanTier.Starter, (await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == org.Id)).PlanTier);
    }

    [Theory]
    [InlineData(PlanTier.Starter, SubscriptionStatus.Active)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Active)]
    [InlineData(PlanTier.Pro, SubscriptionStatus.Trialing)]
    public async Task SyncFromSubscriptionAsync_ScaleOpen_PaidSubscription_KeepsTheStoredTier(PlanTier stored, SubscriptionStatus status)
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(stored, status));
        var service = new EntitlementService(db, Config(ScaleOpen));

        await service.SyncFromSubscriptionAsync(org.Id);

        await AssertStoredPlanUntouchedAsync(db, org);
    }

    [Fact]
    public async Task SyncFromSubscriptionAsync_ScaleOpen_NoSubscription_WritesNothing()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None));
        var service = new EntitlementService(db, Config(ScaleOpen));

        await service.SyncFromSubscriptionAsync(org.Id);

        await AssertStoredPlanUntouchedAsync(db, org);
    }

    // ─── Configuration problems fail closed ──────────────────────────────────────

    [Theory]
    [InlineData("yes", "Scale")]
    [InlineData("true", "Enterprise")]
    [InlineData("1", "Pro")]
    public async Task GetEntitlementAsync_InvalidConfiguration_FailsClosedToThePaidTier(string enabled, string tier)
    {
        // The startup refuses these values; a host that skipped the check must still never open the access.
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);
        var service = new EntitlementService(db, Config(null,
            ("Entitlement:OpenAccess:Enabled", enabled), ("Entitlement:OpenAccess:Tier", tier)));

        var result = await service.GetEntitlementAsync(org.Id);

        Assert.Equal("Starter", result.PlanTier);
        Assert.False(result.CanAddProperty);
        Assert.False(result.OpenAccess);
    }

    [Fact]
    public async Task GetEntitlementAsync_TheSwitchIsReadAtEveryCall()
    {
        await using var db = NewDb();
        var org = await SeedAsync(db, OrgWith(PlanTier.Starter, SubscriptionStatus.None), properties: 3);
        var configuration = Config();
        var service = new EntitlementService(db, configuration);

        Assert.Equal("Starter", (await service.GetEntitlementAsync(org.Id)).PlanTier);

        configuration["Entitlement:OpenAccess:Enabled"] = "true";
        Assert.Equal("Scale", (await service.GetEntitlementAsync(org.Id)).PlanTier);

        configuration["Entitlement:OpenAccess:Enabled"] = "false";
        Assert.Equal("Starter", (await service.GetEntitlementAsync(org.Id)).PlanTier);
    }
}
