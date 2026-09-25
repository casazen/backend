using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class OrgServiceTests
{
    private static AppDbContext CreateDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task EnsureOrgForUserAsync_NewOrg_StartsOnStarterWithoutSubscription()
    {
        await using var db = CreateDb(nameof(EnsureOrgForUserAsync_NewOrg_StartsOnStarterWithoutSubscription));
        var userId = "auth0|new-user";
        db.Users.Add(new User
        {
            Id = userId,
            Email = "owner@example.com",
            FirstName = "Mario",
            LastName = "Rossi",
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var org = await service.EnsureOrgForUserAsync(userId, "owner@example.com", "Mario Rossi");

        // Paid tiers come only from a Stripe subscription (#274): a new org is always Starter.
        Assert.Equal(PlanTier.Starter, org.PlanTier);
        Assert.Equal(SubscriptionStatus.None, org.SubscriptionStatus);
        Assert.Null(org.SubscriptionId);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(org.Id, user.OrgId);
    }

    [Fact]
    public async Task EnsureOrgForUserAsync_IsIdempotent_DoesNotChangeExistingPlan()
    {
        await using var db = CreateDb(nameof(EnsureOrgForUserAsync_IsIdempotent_DoesNotChangeExistingPlan));
        var userId = "auth0|existing";
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Existing",
            Slug = "org-existing",
            DisplayName = "Existing",
            ContactEmail = "x@y.it",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = "x@y.it",
            FirstName = "A",
            LastName = "B",
            Role = UserRole.PropertyOwner,
            OrgId = orgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var org = await service.EnsureOrgForUserAsync(userId, "x@y.it", "A B");

        Assert.Equal(orgId, org.Id);
        Assert.Equal(PlanTier.Starter, org.PlanTier);
    }

    [Fact]
    public async Task UpdatePlanTierAsync_ChangesTier()
    {
        await using var db = CreateDb(nameof(UpdatePlanTierAsync_ChangesTier));
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Org",
            Slug = "org-test",
            DisplayName = "Org",
            ContactEmail = "x@y.it",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var updated = await service.UpdatePlanTierAsync(orgId, PlanTier.Scale);

        Assert.NotNull(updated);
        Assert.Equal(PlanTier.Scale, updated!.PlanTier);
    }

    // ── UpdateSettingsAsync (A1-22, A1-23) ──────────────────────────────────────────

    [Fact]
    public async Task UpdateSettingsAsync_ValidInput_UpdatesNameDisplayNameSlugAndContactEmail()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_ValidInput_UpdatesNameDisplayNameSlugAndContactEmail));
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "La mia organizzazione",
            Slug = "org-auth0-abc123",
            DisplayName = "La mia organizzazione",
            ContactEmail = string.Empty,
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var updated = await service.UpdateSettingsAsync(
            orgId, "  Villa Parco Rentals  ", "Villa Parco Rentals", "host@example.com", contactEmailPublic: true);

        Assert.NotNull(updated);
        Assert.Equal("Villa Parco Rentals", updated!.Name);
        Assert.Equal("Villa Parco Rentals", updated.DisplayName);
        Assert.Equal("villa-parco-rentals", updated.Slug);
        Assert.Equal("host@example.com", updated.ContactEmail);
        Assert.True(updated.ContactEmailPublic);
    }

    [Fact]
    public async Task UpdateSettingsAsync_UnknownOrg_ReturnsNull()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_UnknownOrg_ReturnsNull));
        var service = new OrgService(db);

        var updated = await service.UpdateSettingsAsync(
            Guid.NewGuid(), "Name", "slug", "x@y.it", contactEmailPublic: false);

        Assert.Null(updated);
    }

    [Fact]
    public async Task UpdateSettingsAsync_SlugAlreadyUsedByAnotherOrg_ThrowsConflict()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_SlugAlreadyUsedByAnotherOrg_ThrowsConflict));
        var takenOrgId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        db.Orgs.AddRange(
            new OrgEntity
            {
                Id = takenOrgId,
                Name = "Other",
                Slug = "villa-mare",
                DisplayName = "Other",
                ContactEmail = "other@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            },
            new OrgEntity
            {
                Id = orgId,
                Name = "Mine",
                Slug = "org-mine",
                DisplayName = "Mine",
                ContactEmail = "mine@example.com",
                PlanTier = PlanTier.Starter,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.UpdateSettingsAsync(orgId, "Mine", "villa-mare", "mine@example.com", contactEmailPublic: false));

        Assert.Equal("org_slug_taken", ex.Code);
        var reloaded = await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId);
        Assert.Equal("org-mine", reloaded.Slug);
    }

    [Fact]
    public async Task UpdateSettingsAsync_SameSlugAsBefore_DoesNotThrowConflict()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_SameSlugAsBefore_DoesNotThrowConflict));
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Mine",
            Slug = "villa-mare",
            DisplayName = "Mine",
            ContactEmail = "mine@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var updated = await service.UpdateSettingsAsync(
            orgId, "Mine Updated", "Villa Mare", "mine@example.com", contactEmailPublic: false);

        Assert.NotNull(updated);
        Assert.Equal("villa-mare", updated!.Slug);
        Assert.Equal("Mine Updated", updated.Name);
    }

    [Theory]
    [InlineData("book")]
    [InlineData("admin")]
    public async Task UpdateSettingsAsync_ReservedSlug_ThrowsDomainRuleException(string reserved)
    {
        await using var db = CreateDb($"{nameof(UpdateSettingsAsync_ReservedSlug_ThrowsDomainRuleException)}-{reserved}");
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Mine",
            Slug = "org-mine",
            DisplayName = "Mine",
            ContactEmail = "mine@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.UpdateSettingsAsync(orgId, "Mine", reserved, "mine@example.com", contactEmailPublic: false));

        Assert.Equal("org_slug_reserved", ex.Code);
    }

    [Fact]
    public async Task UpdateSettingsAsync_ContactEmailPublicDefaultsFalse_OnNewOrg()
    {
        // A1-22/A1-23: the opt-in is off unless the caller explicitly turns it on.
        await using var db = CreateDb(nameof(UpdateSettingsAsync_ContactEmailPublicDefaultsFalse_OnNewOrg));
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Mine",
            Slug = "org-mine",
            DisplayName = "Mine",
            ContactEmail = string.Empty,
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        Assert.False((await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId)).ContactEmailPublic);
    }

    // ── GetPublicBySlugAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPublicBySlugAsync_ReturnsActiveOrg_ForKnownSlug()
    {
        await using var db = CreateDb(nameof(GetPublicBySlugAsync_ReturnsActiveOrg_ForKnownSlug));
        var slug = "branded-org";
        db.Orgs.Add(new OrgEntity
        {
            Name = "Branded",
            Slug = slug,
            DisplayName = "Branded Org",
            ContactEmail = "branded@example.com",
            ThemeColor = "#2563eb",
            PlanTier = PlanTier.Pro,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var result = await service.GetPublicBySlugAsync(slug);

        Assert.NotNull(result);
        Assert.Equal(slug, result!.Slug);
        Assert.Equal("Branded Org", result.DisplayName);
    }

    [Fact]
    public async Task GetPublicBySlugAsync_ReturnsNull_ForUnknownSlug()
    {
        await using var db = CreateDb(nameof(GetPublicBySlugAsync_ReturnsNull_ForUnknownSlug));
        var service = new OrgService(db);

        var result = await service.GetPublicBySlugAsync("does-not-exist");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPublicBySlugAsync_ReturnsNull_WhenOrgIsInactive()
    {
        // Inactive orgs must not be surfaced on the public booking site.
        await using var db = CreateDb(nameof(GetPublicBySlugAsync_ReturnsNull_WhenOrgIsInactive));
        db.Orgs.Add(new OrgEntity
        {
            Name = "Inactive Org",
            Slug = "inactive-org",
            DisplayName = "Inactive Org",
            ContactEmail = "inactive@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = false,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var result = await service.GetPublicBySlugAsync("inactive-org");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPublicBySlugAsync_IsSlugCaseSensitive()
    {
        // Slug matching must be exact (lowercase by convention); uppercase variant returns null.
        await using var db = CreateDb(nameof(GetPublicBySlugAsync_IsSlugCaseSensitive));
        db.Orgs.Add(new OrgEntity
        {
            Name = "Case Org",
            Slug = "my-org",
            DisplayName = "My Org",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var exact = await service.GetPublicBySlugAsync("my-org");
        var upper = await service.GetPublicBySlugAsync("MY-ORG");

        Assert.NotNull(exact);
        // In-memory EF uses ordinal comparison; upper should not match
        // (consistent with production Postgres case-sensitive collation on the slug column).
        Assert.Null(upper);
    }
}
