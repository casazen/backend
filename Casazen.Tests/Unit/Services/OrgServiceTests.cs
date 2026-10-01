using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
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
    public async Task EnsureOrgForUserAsync_UserOrgIdPointsToSupplierOrg_ProvisionsNewHostOrgAndKeepsSupplierLink()
    {
        // A1-40: a supplier registers first (OrgId and SupplierOrgId both point at the Supplier org, as
        // SupplierService links them), then does the host onboarding. The Supplier org must never become the
        // host org, and the existing supplier link must survive.
        await using var db = CreateDb(
            nameof(EnsureOrgForUserAsync_UserOrgIdPointsToSupplierOrg_ProvisionsNewHostOrgAndKeepsSupplierLink));
        var userId = "auth0|supplier-then-host";
        var supplierOrgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = supplierOrgId,
            Name = "Fornitore Srl",
            Slug = "supplier-org",
            DisplayName = "Fornitore Srl",
            ContactEmail = "fornitore@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = "fornitore@example.com",
            FirstName = "Mario",
            LastName = "Fornitore",
            Role = UserRole.Supplier,
            OrgId = supplierOrgId,
            SupplierOrgId = supplierOrgId,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var org = await service.EnsureOrgForUserAsync(userId, "fornitore@example.com", "Mario Fornitore");

        Assert.NotEqual(supplierOrgId, org.Id);
        Assert.Equal(OrgType.Host, org.OrgType);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(org.Id, user.OrgId);
        Assert.Equal(supplierOrgId, user.SupplierOrgId);
    }

    [Fact]
    public async Task EnsureOrgForUserAsync_UserOrgIdPointsToSupplierOrgWithoutSupplierOrgIdSet_BackfillsSupplierLink()
    {
        // Legacy data (pre-SU-08): OrgId already points at the Supplier org but SupplierOrgId was never set.
        // The host onboarding must not lose that supplier link once it stops reusing the Supplier org (A1-40).
        await using var db = CreateDb(
            nameof(EnsureOrgForUserAsync_UserOrgIdPointsToSupplierOrgWithoutSupplierOrgIdSet_BackfillsSupplierLink));
        var userId = "auth0|legacy-supplier";
        var supplierOrgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = supplierOrgId,
            Name = "Fornitore Legacy",
            Slug = "supplier-legacy",
            DisplayName = "Fornitore Legacy",
            ContactEmail = "legacy@example.com",
            OrgType = OrgType.Supplier,
            IsActive = true,
        });
        db.Users.Add(new User
        {
            Id = userId,
            Email = "legacy@example.com",
            FirstName = "Anna",
            LastName = "Legacy",
            Role = UserRole.Supplier,
            OrgId = supplierOrgId,
            SupplierOrgId = null,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var service = new OrgService(db);
        var org = await service.EnsureOrgForUserAsync(userId, "legacy@example.com", "Anna Legacy");

        Assert.Equal(OrgType.Host, org.OrgType);
        var user = await db.Users.SingleAsync(u => u.Id == userId);
        Assert.Equal(org.Id, user.OrgId);
        Assert.Equal(supplierOrgId, user.SupplierOrgId);
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
    public async Task EnsureOrgForUserAsync_NewOrg_NeutralSlugAndContactEmailNotPublic()
    {
        // A1-23: the slug never carries the identity-provider id; A1-22: the contact email is published only on opt-in.
        await using var db = CreateDb(nameof(EnsureOrgForUserAsync_NewOrg_NeutralSlugAndContactEmailNotPublic));
        var userId = "google-oauth2|109876543210987654321";
        db.Users.Add(new User
        {
            Id = userId,
            Email = "mario@example.com",
            FirstName = "Mario",
            LastName = "Rossi",
            Role = UserRole.PropertyOwner,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var org = await new OrgService(db).EnsureOrgForUserAsync(userId, "mario@example.com", "Mario Rossi");

        Assert.Matches("^org-[a-z0-9]{8}$", org.Slug);
        Assert.DoesNotContain("google", org.Slug, StringComparison.Ordinal);
        Assert.DoesNotContain("1098765", org.Slug, StringComparison.Ordinal);
        Assert.False(org.ContactEmailPublic);
    }

    [Fact]
    public async Task UpdateSettingsAsync_SlugChanged_PreviousSlugStillResolvesToTheOrg()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_SlugChanged_PreviousSlugStillResolvesToTheOrg));
        var orgId = SeedOrg(db, "org-abcd2345");
        await db.SaveChangesAsync();
        var service = new OrgService(db);

        var updated = await service.UpdateSettingsAsync(orgId, "Villa Mare", "villa-mare", "host@example.com", false);

        Assert.Equal("villa-mare", updated!.Slug);
        var alias = await db.OrgSlugAliases.SingleAsync();
        Assert.Equal(("org-abcd2345", orgId), (alias.Slug, alias.OrgId));
        var byOld = await service.GetPublicBySlugAsync("org-abcd2345");
        Assert.Equal(orgId, byOld!.Id);
        Assert.Equal("villa-mare", byOld.Slug);
    }

    [Fact]
    public async Task UpdateSettingsAsync_SlugIsPreviousSlugOfAnotherOrg_ThrowsConflict()
    {
        // The guests holding the other org's old links must never land on this org's site.
        await using var db = CreateDb(nameof(UpdateSettingsAsync_SlugIsPreviousSlugOfAnotherOrg_ThrowsConflict));
        var otherId = SeedOrg(db, "villa-nuova");
        var orgId = SeedOrg(db, "org-mine2345");
        db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = "villa-vecchia", OrgId = otherId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            new OrgService(db).UpdateSettingsAsync(orgId, "Mine", "villa-vecchia", "mine@example.com", false));

        Assert.Equal("org_slug_taken", ex.Code);
        Assert.Equal("org-mine2345", (await db.Orgs.AsNoTracking().SingleAsync(o => o.Id == orgId)).Slug);
    }

    [Fact]
    public async Task UpdateSettingsAsync_SlugIsSubdomainOfAnotherOrg_ThrowsConflict()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_SlugIsSubdomainOfAnotherOrg_ThrowsConflict));
        var otherId = SeedOrg(db, "org-other234");
        (await db.Orgs.FindAsync(otherId))!.Subdomain = "villa-sole";
        var orgId = SeedOrg(db, "org-mine2345");
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() =>
            new OrgService(db).UpdateSettingsAsync(orgId, "Mine", "villa-sole", "mine@example.com", false));

        Assert.Equal("org_slug_taken", ex.Code);
    }

    [Fact]
    public async Task UpdateSettingsAsync_BackToOwnPreviousSlug_SwapsCurrentSlugAndAlias()
    {
        await using var db = CreateDb(nameof(UpdateSettingsAsync_BackToOwnPreviousSlug_SwapsCurrentSlugAndAlias));
        var orgId = SeedOrg(db, "org-abcd2345");
        await db.SaveChangesAsync();
        var service = new OrgService(db);
        await service.UpdateSettingsAsync(orgId, "Villa", "villa-mare", "host@example.com", false);

        var updated = await service.UpdateSettingsAsync(orgId, "Villa", "org-abcd2345", "host@example.com", false);

        Assert.Equal("org-abcd2345", updated!.Slug);
        var alias = await db.OrgSlugAliases.AsNoTracking().SingleAsync();
        Assert.Equal("villa-mare", alias.Slug);
    }

    [Fact]
    public async Task UpdateSettingsAsync_LegacySlugSentUnchanged_KeepsItWithoutValidation()
    {
        // Orgs provisioned before A1-23 have slugs longer than a DNS label: saving the name must not force a new slug.
        await using var db = CreateDb(nameof(UpdateSettingsAsync_LegacySlugSentUnchanged_KeepsItWithoutValidation));
        var legacy = "org-google-oauth2-109876543210987654321-and-some-more-characters-to-pass-63";
        var orgId = SeedOrg(db, legacy);
        await db.SaveChangesAsync();

        var updated = await new OrgService(db).UpdateSettingsAsync(orgId, "Villa", legacy, "host@example.com", true);

        Assert.Equal(legacy, updated!.Slug);
        Assert.Equal("Villa", updated.Name);
        Assert.Empty(await db.OrgSlugAliases.ToListAsync());
    }

    [Fact]
    public async Task CheckSlugAvailabilityAsync_VariousSlugs_ReturnsNormalizedSlugAndReason()
    {
        await using var db = CreateDb(nameof(CheckSlugAvailabilityAsync_VariousSlugs_ReturnsNormalizedSlugAndReason));
        var otherId = SeedOrg(db, "villa-presa");
        db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = "villa-usata", OrgId = otherId, CreatedAt = DateTime.UtcNow });
        var orgId = SeedOrg(db, "org-mine2345");
        db.OrgSlugAliases.Add(new OrgSlugAlias { Slug = "la-mia-vecchia", OrgId = orgId, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        var service = new OrgService(db);

        Assert.Equal(new OrgSlugAvailability("villa-libera", true, null), await service.CheckSlugAvailabilityAsync(orgId, "Villa Libera"));
        Assert.Equal(new OrgSlugAvailability("org-mine2345", true, null), await service.CheckSlugAvailabilityAsync(orgId, "org-mine2345"));
        Assert.Equal(new OrgSlugAvailability("la-mia-vecchia", true, null), await service.CheckSlugAvailabilityAsync(orgId, "la-mia-vecchia"));
        Assert.Equal(new OrgSlugAvailability("villa-presa", false, "org_slug_taken"), await service.CheckSlugAvailabilityAsync(orgId, "villa-presa"));
        Assert.Equal(new OrgSlugAvailability("villa-usata", false, "org_slug_taken"), await service.CheckSlugAvailabilityAsync(orgId, "villa-usata"));
        Assert.Equal(new OrgSlugAvailability("admin", false, "org_slug_reserved"), await service.CheckSlugAvailabilityAsync(orgId, "Admin"));
        Assert.Equal(new OrgSlugAvailability("x", false, "org_slug_invalid"), await service.CheckSlugAvailabilityAsync(orgId, "x!"));
        Assert.Null(await service.CheckSlugAvailabilityAsync(Guid.NewGuid(), "villa-libera"));
    }

    private static Guid SeedOrg(AppDbContext db, string slug)
    {
        var org = new OrgEntity
        {
            Name = "Org",
            Slug = slug,
            DisplayName = "Org",
            ContactEmail = "org@example.com",
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        return org.Id;
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
