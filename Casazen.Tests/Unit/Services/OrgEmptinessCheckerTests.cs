using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02: the guard of the one destructive-looking move of the invitations, a person leaving its empty org for the one that
/// invited it. The list of tables is read from the model, and these tests are the tripwire that keeps it that way: a tenant
/// table added tomorrow is part of the "org is empty" rule from the first day, or this suite fails.
/// </summary>
public class OrgEmptinessCheckerTests
{
    private const string OwnerId = "auth0|anna";

    private static AppDbContext NewDb() => OrgTeamTestData.NewDb();

    private static async Task<Guid> SeedEmptyOrgAsync(AppDbContext db)
    {
        var org = OrgTeamTestData.AddOrg(db);
        OrgTeamTestData.AddUser(db, OwnerId, org.Id, UserRole.PropertyOwner);
        OrgTeamTestData.AddMember(db, OwnerId, org.Id, OrgRole.Owner);
        db.ConsentRecords.Add(new ConsentRecord { UserId = OwnerId, OrgId = org.Id, Type = ConsentType.Tos, Version = "v1" });
        db.SignupAttributions.Add(new SignupAttribution { OrgId = org.Id, UtmSource = "google" });
        await db.SaveChangesAsync();
        return org.Id;
    }

    // ─── The tables ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnboardingRows_AreExactlyWhatTheOnboardingLeavesAndAllTenantOwned()
    {
        // A new entry here is a decision, not a convenience: it makes an org with that table filled still "empty".
        Assert.Equal(
            [typeof(ConsentRecord), typeof(OrgSlugAlias), typeof(SignupAttribution)],
            OrgEmptinessChecker.OnboardingRows.OrderBy(t => t.Name, StringComparer.Ordinal));
        Assert.All(OrgEmptinessChecker.OnboardingRows, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), t.Name));
    }

    [Fact]
    public void CheckedTenantTables_EveryTenantOwnedEntityOfTheModel_IsCheckedOrAnExplicitException()
    {
        using var db = NewDb();

        var tenantOwned = db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && !e.IsOwned() && e.BaseType is null)
            .Select(e => e.ClrType)
            .ToHashSet();
        var checkedTables = OrgEmptinessChecker.CheckedTenantTables(db).ToHashSet();

        var neither = tenantOwned
            .Where(t => !checkedTables.Contains(t) && !OrgEmptinessChecker.OnboardingRows.Contains(t) && t != typeof(OrgMember))
            .Select(t => t.Name)
            .ToList();
        Assert.True(neither.Count == 0, "Tenant tables the empty-org check does not look at: " + string.Join(", ", neither));
        Assert.Empty(checkedTables.Except(tenantOwned));

        // The ones that matter most are in, the new tables of the team included.
        foreach (var table in new[]
                 {
                     typeof(Property), typeof(Booking), typeof(Guest), typeof(Payment), typeof(LeaseContract), typeof(PlatformInvoice),
                     typeof(CalendarBlock), typeof(OrgInvitation), typeof(OrgSiteDocument), typeof(PropertyDocument),
                 })
            Assert.Contains(table, checkedTables);

        // OrgMember is checked in its own way (the person's own row is expected), the onboarding rows are the exception.
        Assert.DoesNotContain(typeof(OrgMember), checkedTables);
        Assert.All(OrgEmptinessChecker.OnboardingRows, t => Assert.DoesNotContain(t, checkedTables));
        Assert.True(checkedTables.Count >= 30, $"Only {checkedTables.Count} tenant tables are checked.");
    }

    /// <summary>
    /// The tripwire in action: for every table the check looks at, a single row with the org's id makes the org "in use".
    /// A table whose entity cannot be built with its defaults is listed, and the test fails until somebody gives it a row
    /// here: a table can never silently fall out of the check.
    /// </summary>
    [Fact]
    public async Task CheckAsync_AOneRowInAnyTenantTable_MakesTheOrgInUse()
    {
        List<Type> tables;
        using (var model = NewDb())
            tables = OrgEmptinessChecker.CheckedTenantTables(model).ToList();

        var notExercised = new List<string>();
        var notBlocking = new List<string>();
        foreach (var table in tables)
        {
            await using var db = NewDb();
            var orgId = await SeedEmptyOrgAsync(db);
            if (!await TryAddRowAsync(db, table, orgId))
            {
                notExercised.Add(table.Name);
                continue;
            }

            var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);
            if (result.IsEmpty || !result.Blockers.Contains($"data:{table.Name}"))
                notBlocking.Add(table.Name);
        }

        Assert.True(
            notBlocking.Count == 0,
            "A row in these tenant tables does not stop an org from being left: " + string.Join(", ", notBlocking));
        Assert.True(
            notExercised.Count == 0,
            "These tenant tables cannot be built from their defaults by this test, so nothing proves they are checked: " +
            string.Join(", ", notExercised) + ". Give them a row in TryAddRowAsync.");
    }

    private static async Task<bool> TryAddRowAsync(AppDbContext db, Type table, Guid orgId)
    {
        object? row;
        try
        {
            row = Activator.CreateInstance(table);
        }
        catch (MissingMethodException)
        {
            return false;
        }

        if (row is null)
            return false;

        var orgIdProperty = table.GetProperty(nameof(ITenantOwned.OrgId));
        if (orgIdProperty is null || !orgIdProperty.CanWrite)
            return false;
        orgIdProperty.SetValue(row, orgId);

        // Specific rows for the entities whose defaults are not a valid row of the in-memory provider.
        PrepareRow(row, db);

        db.Add(row);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Gives an entity the few values the in-memory provider insists on (required members without a default).</summary>
    private static void PrepareRow(object row, AppDbContext db)
    {
        switch (row)
        {
            case OrgSlugAlias alias:
                alias.Slug = $"alias-{Guid.NewGuid():N}";
                break;
        }
    }

    // ─── The org itself ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_AnUntouchedOrg_IsEmpty()
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_AnOrgThatDoesNotExist_IsNotEmpty()
    {
        await using var db = NewDb();

        var result = await new OrgEmptinessChecker(db).CheckAsync(Guid.NewGuid(), OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Equal(["not_found"], result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_ASupplierOrg_IsNotEmpty()
    {
        await using var db = NewDb();
        var org = OrgTeamTestData.AddOrg(db, OrgType.Supplier);
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(org.Id, OwnerId);

        Assert.Contains("not_a_host_org", result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_SeveralReasons_AreAllReported()
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        var org = await db.Orgs.SingleAsync(o => o.Id == orgId);
        org.PlanTier = PlanTier.Scale;
        org.StripeCustomerId = "cus_1";
        org.SubscriptionStatus = SubscriptionStatus.Active;
        org.SubscriptionId = "sub_1";
        org.StripeConnectedAccountId = "acct_1";
        org.CustomDomain = "x.example.com";
        org.LogoUrl = "https://x/logo.png";
        OrgTeamTestData.AddUser(db, "auth0|other", orgId);
        db.Properties.Add(new Property { OwnerId = OwnerId, OrgId = orgId, Name = "P", Address = "A", City = "C" });
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Equal(
            ["branding", "connect_account", "data:Property", "domain", "other_users", "plan", "stripe_customer", "subscription"],
            result.Blockers.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(OrgMemberStatus.Active)]
    [InlineData(OrgMemberStatus.Deactivated)]
    public async Task CheckAsync_AnotherMemberOfTheOrg_BlocksIt(OrgMemberStatus status)
    {
        await using var db = NewDb();
        var orgId = await SeedEmptyOrgAsync(db);
        OrgTeamTestData.AddUser(db, "auth0|colleague", orgId: null);
        OrgTeamTestData.AddMember(db, "auth0|colleague", orgId, OrgRole.Collaborator, status);
        await db.SaveChangesAsync();

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.Contains("other_members", result.Blockers);
    }

    [Fact]
    public async Task CheckAsync_ReadsTheOrgWhateverTheTenantOfTheRequest_SoftDeletedPropertiesIncluded()
    {
        var database = Guid.NewGuid().ToString();
        Guid orgId;
        await using (var seed = OrgTeamTestData.NewDb(database))
        {
            orgId = await SeedEmptyOrgAsync(seed);
            seed.Properties.Add(new Property { OwnerId = OwnerId, OrgId = orgId, Name = "P", Address = "A", City = "C", IsDeleted = true });
            await seed.SaveChangesAsync();
        }

        // The request belongs to another org: the tenant filter would hide the rows of this one, and the soft-delete
        // filter the deleted property.
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(database).Options,
            new FixedTenantContext(Guid.NewGuid()));

        var result = await new OrgEmptinessChecker(db).CheckAsync(orgId, OwnerId);

        Assert.False(result.IsEmpty);
        Assert.Contains("data:Property", result.Blockers);
    }

    private sealed class FixedTenantContext(Guid orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
