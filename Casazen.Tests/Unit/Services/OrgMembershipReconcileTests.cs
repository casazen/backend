using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-01: the reconcile command. It gives an org member row to the single owner of each host org that has none (the rule
/// of the backfill of the migration), re-aligns the memberships with the org roles, reports what it cannot decide and
/// never guesses; a dry run reports exactly what an applied run does and saves nothing; a second run finds nothing to do.
/// </summary>
public class OrgMembershipReconcileTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 30, 0, TimeSpan.Zero);

    private readonly Mock<IUserAuthorizationCache> _cache = new();
    private readonly string _database = Guid.NewGuid().ToString();

    private OrgMembershipService NewService(AppDbContext db) =>
        new(db, _cache.Object, NullLogger<OrgMembershipService>.Instance, new FixedTimeProvider(Now));

    private static IEnumerable<string> Codes(IEnumerable<OrgMembershipFix> fixes) => fixes.Select(f => f.Code);

    private static IEnumerable<string> Codes(IEnumerable<OrgMembershipIssue> issues) => issues.Select(i => i.Code);

    // ─── The owner of every host org ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_OwnerWithoutOrgMember_GetsTheOwnerRowAndTheAccountMembership()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddMembership(db, "auth0|owner", "short-rent", "property_owner");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Equal([OrgMembershipReconcileCodes.OwnerCreated, OrgMembershipReconcileCodes.AccountMembershipAdded], Codes(report.Fixes));
        Assert.Empty(report.Issues);
        Assert.Equal(1, report.HostOrgsScanned);
        Assert.Equal(1, report.MembersScanned);
        await using var verify = NewDb(_database);
        var member = await verify.OrgMembers.AsNoTracking().SingleAsync();
        Assert.Equal(("auth0|owner", org.Id, OrgRole.Owner, OrgMemberStatus.Active, PropertyScope.All),
            (member.UserId, member.OrgId, member.Role, member.Status, member.PropertyScope));
        Assert.Equal(Now.UtcDateTime, member.CreatedAt);
        Assert.Equal(["account/org_owner", "short-rent/property_owner"], await MembershipsOfAsync(verify, "auth0|owner"));
        _cache.Verify(c => c.Invalidate("auth0|owner"), Times.Once);
    }

    [Theory]
    [InlineData(UserRole.PropertyOwner)]
    [InlineData(UserRole.LongTermLandlord)]
    public async Task ReconcileAsync_OwnerRolesOfTheUser_AreOwners(UserRole role)
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, role);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Contains(OrgMembershipReconcileCodes.OwnerCreated, Codes(report.Fixes));
    }

    [Fact]
    public async Task ReconcileAsync_PlatformAdminThatSetUpItsOwnHostOrg_IsItsOwner()
    {
        // An admin keeps the Admin role when it onboards as a host: it shows as the owner of its org by its owner membership.
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|staff-host", org.Id, UserRole.Admin);
        AddMembership(db, "auth0|staff-host", "short-rent", "property_owner");
        AddMembership(db, "auth0|staff-host", "admin", "platform_admin");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Contains(OrgMembershipReconcileCodes.OwnerCreated, Codes(report.Fixes));
        await using var verify = NewDb(_database);
        Assert.Equal(
            ["account/org_owner", "admin/platform_admin", "short-rent/property_owner"],
            await MembershipsOfAsync(verify, "auth0|staff-host"));
    }

    [Fact]
    public async Task ReconcileAsync_PlatformAdminWithoutAHostMembership_IsNotAnOwner()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|staff", org.Id, UserRole.Admin);
        AddMembership(db, "auth0|staff", "admin", "platform_admin");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.DoesNotContain(OrgMembershipReconcileCodes.OwnerCreated, Codes(report.Fixes));
        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
    }

    [Fact]
    public async Task ReconcileAsync_SupplierOrg_IsNoOrgTeam()
    {
        // A supplier org may hold several accounts (PL-05) and has no owner role of a host: nothing is created there.
        await using var db = NewDb(_database);
        var supplierOrg = AddOrg(db, OrgType.Supplier);
        AddUser(db, "auth0|supplier-1", supplierOrg.Id, UserRole.PropertyOwner);
        AddUser(db, "auth0|supplier-2", supplierOrg.Id, UserRole.Supplier);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Empty(report.Fixes);
        Assert.Empty(report.Issues);
        Assert.Equal(0, report.HostOrgsScanned);
        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
    }

    [Fact]
    public async Task ReconcileAsync_OrgWithSeveralOwnerCandidates_CreatesNoOneAndReportsThemAll()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|first", org.Id, UserRole.PropertyOwner);
        AddUser(db, "auth0|second", org.Id, UserRole.LongTermLandlord);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Empty(report.Fixes);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(OrgMembershipReconcileCodes.OwnerAmbiguous, issue.Code);
        Assert.Equal(org.Id, issue.OrgId);
        Assert.Equal(["auth0|first", "auth0|second"], issue.UserIds);
        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
        Assert.Empty(await verify.UserContextMemberships.ToListAsync());
    }

    [Fact]
    public async Task ReconcileAsync_SingleOwnerWithOtherUsersInTheOrg_CreatesTheOwnerAndLeavesTheOthersListed()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddMembership(db, "auth0|owner", "short-rent", "property_owner");
        AddUser(db, "auth0|manager", org.Id, UserRole.PropertyManager);
        AddUser(db, "auth0|nobody", org.Id, UserRole.None);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Contains(OrgMembershipReconcileCodes.OwnerCreated, Codes(report.Fixes));
        var issue = Assert.Single(report.Issues);
        Assert.Equal(OrgMembershipReconcileCodes.UserWithoutMember, issue.Code);
        Assert.Equal(["auth0|manager", "auth0|nobody"], issue.UserIds);
        await using var verify = NewDb(_database);
        // Their role in the org is not guessed: no org member for them.
        Assert.Equal(["auth0|owner"], await verify.OrgMembers.Select(m => m.UserId).ToListAsync());
    }

    [Fact]
    public async Task ReconcileAsync_UserWithoutAnOrgOrWithAnOtherRoleAlone_IsNotTouched()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|no-org", orgId: null, UserRole.PropertyOwner);
        AddUser(db, "auth0|manager-alone", org.Id, UserRole.PropertyManager);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Empty(report.Fixes);
        Assert.Equal(["auth0|manager-alone"], Assert.Single(report.Issues).UserIds);
    }

    // ─── The memberships follow the org roles ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_AdminWithAnOwnerRentalRow_IsAlignedToItsRole()
    {
        // A member can never keep an owner's rental row (a token cannot bring it back either: the veto of the contexts).
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|admin", org.Id);
        AddMember(db, "auth0|admin", org.Id, OrgRole.Admin);
        AddMembership(db, "auth0|admin", "short-rent", "property_owner");
        AddMembership(db, "auth0|admin", "long-rent", "property_manager");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Equal(
            [OrgMembershipReconcileCodes.AccountMembershipAdded, OrgMembershipReconcileCodes.HostMembershipChanged],
            Codes(report.Fixes).Order());
        await using var verify = NewDb(_database);
        Assert.Equal(
            ["account/org_admin", "long-rent/property_manager", "short-rent/property_manager"],
            await MembershipsOfAsync(verify, "auth0|admin"));
    }

    [Fact]
    public async Task ReconcileAsync_AdminMemberHoldingAnOwnerRow_IsNotASecondOwnerCandidate()
    {
        // The org member row is what says who someone is: the stray owner's row of the administrator is drift to align in
        // this very run, not a reason to leave the org without an owner as ambiguous (one run converges).
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddMembership(db, "auth0|owner", "short-rent", "property_owner");
        AddUser(db, "auth0|admin", org.Id);
        AddMember(db, "auth0|admin", org.Id, OrgRole.Admin);
        AddMembership(db, "auth0|admin", "short-rent", "property_owner");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.DoesNotContain(OrgMembershipReconcileCodes.OwnerAmbiguous, Codes(report.Issues));
        Assert.Contains(report.Fixes, f => f.Code == OrgMembershipReconcileCodes.OwnerCreated && f.UserId == "auth0|owner");
        Assert.Contains(report.Fixes, f => f.Code == OrgMembershipReconcileCodes.HostMembershipChanged && f.UserId == "auth0|admin");
        await using var verify = NewDb(_database);
        Assert.Equal(["auth0|admin", "auth0|owner"], (await verify.OrgMembers.Select(m => m.UserId).ToListAsync()).Order());
        Assert.Equal(["account/org_admin", "short-rent/property_manager"], await MembershipsOfAsync(verify, "auth0|admin"));
    }

    [Fact]
    public async Task ReconcileAsync_CollaboratorWithAStrayAccountRow_LosesIt()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|collab", org.Id);
        AddMember(db, "auth0|collab", org.Id, OrgRole.Collaborator);
        AddMembership(db, "auth0|collab", "account", "org_admin");
        AddMembership(db, "auth0|collab", "short-rent", "staff");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Equal([OrgMembershipReconcileCodes.AccountMembershipRemoved], Codes(report.Fixes));
        await using var verify = NewDb(_database);
        Assert.Equal(["short-rent/staff"], await MembershipsOfAsync(verify, "auth0|collab"));
    }

    [Fact]
    public async Task ReconcileAsync_AccountRowOfAWrongRole_IsRepointed()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|accountant", org.Id);
        AddMember(db, "auth0|accountant", org.Id, OrgRole.Accountant);
        AddMembership(db, "auth0|accountant", "account", "org_admin");
        AddMembership(db, "auth0|accountant", "short-rent", "accountant");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Equal([OrgMembershipReconcileCodes.AccountMembershipChanged], Codes(report.Fixes));
        await using var verify = NewDb(_database);
        Assert.Equal(["account/org_accountant", "short-rent/accountant"], await MembershipsOfAsync(verify, "auth0|accountant"));
    }

    [Fact]
    public async Task ReconcileAsync_AccountRowOfAUserThatIsNotAMember_IsALeftoverAndIsRemoved()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|ex-member", org.Id);
        AddMembership(db, "auth0|ex-member", "account", "org_admin");
        AddMembership(db, "auth0|ex-member", "short-rent", "staff");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        var fix = Assert.Single(report.Fixes);
        Assert.Equal(OrgMembershipReconcileCodes.AccountMembershipRemoved, fix.Code);
        Assert.Equal("auth0|ex-member", fix.UserId);
        await using var verify = NewDb(_database);
        // Only the account row: the rental row of a user that is not a member is not the reconcile's to remove.
        Assert.Equal(["short-rent/staff"], await MembershipsOfAsync(verify, "auth0|ex-member"));
    }

    [Fact]
    public async Task ReconcileAsync_MemberOfAnotherOrgThanItsUsers_IsReportedAndNotAligned()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        var other = AddOrg(db);
        AddUser(db, "auth0|moved", other.Id);
        AddMember(db, "auth0|moved", org.Id, OrgRole.Admin);
        AddMembership(db, "auth0|moved", "short-rent", "property_owner");
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.DoesNotContain(OrgMembershipReconcileCodes.AccountMembershipAdded, Codes(report.Fixes));
        var issue = report.Issues.Single(i => i.Code == OrgMembershipReconcileCodes.OrgMismatch);
        Assert.Equal(["auth0|moved"], issue.UserIds);
        await using var verify = NewDb(_database);
        Assert.Equal(["short-rent/property_owner"], await MembershipsOfAsync(verify, "auth0|moved"));
    }

    [Fact]
    public async Task ReconcileAsync_OwnerAndMemberWithoutAnyRentalMembership_AreReported()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        var otherOrg = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddUser(db, "auth0|member", otherOrg.Id);
        AddMember(db, "auth0|member", otherOrg.Id, OrgRole.PropertyManager);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Contains(report.Issues, i => i.Code == OrgMembershipReconcileCodes.OwnerWithoutHostMembership && i.UserIds.SequenceEqual(["auth0|owner"]));
        Assert.Contains(report.Issues, i => i.Code == OrgMembershipReconcileCodes.MemberWithoutHostMembership && i.UserIds.SequenceEqual(["auth0|member"]));
        await using var verify = NewDb(_database);
        // Its areas are unknown: nothing is added for the member.
        Assert.Empty(await MembershipsOfAsync(verify, "auth0|member"));
    }

    [Fact]
    public async Task ReconcileAsync_SeveralOwnerMembersInAnOrg_AreReported()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|a", org.Id);
        AddUser(db, "auth0|b", org.Id);
        AddMember(db, "auth0|a", org.Id, OrgRole.Owner);
        AddMember(db, "auth0|b", org.Id, OrgRole.Owner);
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        var issue = report.Issues.Single(i => i.Code == OrgMembershipReconcileCodes.SeveralOwners);
        Assert.Equal(org.Id, issue.OrgId);
        Assert.Equal(["auth0|a", "auth0|b"], issue.UserIds);
    }

    [Fact]
    public async Task ReconcileAsync_DeactivatedMember_IsAlignedLikeAnyOtherAndStaysDeactivated()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|off", org.Id);
        AddMember(db, "auth0|off", org.Id, OrgRole.Admin, OrgMemberStatus.Deactivated);
        AddMembership(db, "auth0|off", "short-rent", "property_manager");
        await db.SaveChangesAsync();

        await NewService(db).ReconcileAsync(dryRun: false);

        await using var verify = NewDb(_database);
        Assert.Equal(OrgMemberStatus.Deactivated, (await verify.OrgMembers.SingleAsync()).Status);
        Assert.Equal(["account/org_admin", "short-rent/property_manager"], await MembershipsOfAsync(verify, "auth0|off"));
    }

    // ─── Dry run, idempotence ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReconcileAsync_DryRun_ReportsWhatAnAppliedRunDoesAndSavesNothing()
    {
        await using var db = NewDb(_database);
        SeedMixedOrgs(db);
        await db.SaveChangesAsync();

        var dry = await NewService(db).ReconcileAsync(dryRun: true);

        await using var afterDry = NewDb(_database);
        // Only the member the seed started with (an administrator): no owner was created, no account membership added.
        Assert.Equal(["auth0|team-admin"], await afterDry.OrgMembers.Select(m => m.UserId).ToListAsync());
        Assert.DoesNotContain(await afterDry.UserContextMemberships.ToListAsync(), m => m.ContextKey == "account" && m.UserId != "auth0|leftover");
        Assert.Contains(await afterDry.UserContextMemberships.ToListAsync(), m => m.UserId == "auth0|leftover" && m.ContextKey == "account");
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);

        await using var db2 = NewDb(_database);
        var applied = await NewService(db2).ReconcileAsync(dryRun: false);

        Assert.True(dry.DryRun);
        Assert.False(applied.DryRun);
        Assert.Equal(Codes(dry.Fixes).Order(), Codes(applied.Fixes).Order());
        Assert.Equal(Codes(dry.Issues).Order(), Codes(applied.Issues).Order());
        Assert.Equal(dry.HostOrgsScanned, applied.HostOrgsScanned);
        Assert.Equal(dry.MembersScanned, applied.MembersScanned);
        Assert.NotEmpty(applied.Fixes);
    }

    [Fact]
    public async Task ReconcileAsync_SecondRun_FindsNothingToDo()
    {
        await using var db = NewDb(_database);
        SeedMixedOrgs(db);
        await db.SaveChangesAsync();
        await NewService(db).ReconcileAsync(dryRun: false);
        _cache.Invocations.Clear();

        await using var again = NewDb(_database);
        var second = await NewService(again).ReconcileAsync(dryRun: false);

        Assert.Empty(second.Fixes);
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_InvalidatesTheCacheOfTheTouchedUsersOnlyWhenApplied()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|touched", org.Id, UserRole.PropertyOwner);
        var quietOrg = AddOrg(db);
        AddUser(db, "auth0|quiet", quietOrg.Id);
        AddMember(db, "auth0|quiet", quietOrg.Id, OrgRole.Collaborator);
        AddMembership(db, "auth0|quiet", "short-rent", "staff");
        await db.SaveChangesAsync();

        await NewService(db).ReconcileAsync(dryRun: false);

        _cache.Verify(c => c.Invalidate("auth0|touched"), Times.Once);
        _cache.Verify(c => c.Invalidate("auth0|quiet"), Times.Never);
    }

    [Fact]
    public async Task ReconcileAsync_ReportCarriesIdsOnly_NoNamesNorEmails()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        var user = AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        user.Email = "private.person@example.com";
        user.FirstName = "Maria";
        user.LastName = "Privata";
        await db.SaveChangesAsync();

        var report = await NewService(db).ReconcileAsync(dryRun: true);

        var text = string.Join('|', report.Fixes.Select(f => f.Detail + f.UserId)
            .Concat(report.Issues.Select(i => i.Detail + string.Join(',', i.UserIds))));
        Assert.DoesNotContain("private.person", text);
        Assert.DoesNotContain("Maria", text);
        Assert.DoesNotContain("Privata", text);
    }

    [Fact]
    public async Task ReconcileAsync_EmptyDatabase_IsAnEmptyReport()
    {
        await using var db = NewDb(_database);

        var report = await NewService(db).ReconcileAsync(dryRun: false);

        Assert.Empty(report.Fixes);
        Assert.Empty(report.Issues);
        Assert.Equal(0, report.HostOrgsScanned);
        Assert.Equal(0, report.MembersScanned);
    }

    /// <summary>An owner to create, an ambiguous org, a drifted admin and a leftover account row.</summary>
    private static void SeedMixedOrgs(AppDbContext db)
    {
        var simple = AddOrg(db);
        AddUser(db, "auth0|owner-1", simple.Id, UserRole.PropertyOwner);
        AddMembership(db, "auth0|owner-1", "short-rent", "property_owner");

        var ambiguous = AddOrg(db);
        AddUser(db, "auth0|amb-1", ambiguous.Id, UserRole.PropertyOwner);
        AddUser(db, "auth0|amb-2", ambiguous.Id, UserRole.PropertyOwner);

        var team = AddOrg(db);
        AddUser(db, "auth0|team-owner", team.Id, UserRole.LongTermLandlord);
        AddUser(db, "auth0|team-admin", team.Id);
        AddMember(db, "auth0|team-admin", team.Id, OrgRole.Admin);
        AddMembership(db, "auth0|team-admin", "short-rent", "property_owner");

        AddUser(db, "auth0|leftover", simple.Id);
        AddMembership(db, "auth0|leftover", "account", "org_owner");
    }

    [Fact]
    public async Task ReconcileAsync_ConcurrentChange_IsAConflictAndNothingIsKept()
    {
        // The Postgres run races on the unique indexes (OrgMembershipPostgresTests); here the mapping of that failure to
        // the stable 409 is checked with a context that fails to save.
        await using var db = new ThrowingSaveDbContext(_database);
        db.Database.EnsureCreated();
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        await db.SaveChangesWithoutFailureAsync();
        db.FailOnSave = true;

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => NewService(db).ReconcileAsync(dryRun: false));

        Assert.Equal("org_membership_maintenance_conflict", error.Code);
        Assert.Equal("OrgMembershipMaintenanceConflict", error.MessageKey);
        Assert.Empty(db.ChangeTracker.Entries<OrgMember>());
    }

    /// <summary>A context whose next save fails like a lost race on a unique index (23505).</summary>
    private sealed class ThrowingSaveDbContext(string databaseName)
        : AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options)
    {
        public bool FailOnSave { get; set; }

        public Task<int> SaveChangesWithoutFailureAsync() => base.SaveChangesAsync();

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            FailOnSave
                ? throw new DbUpdateConcurrencyException("lost the race")
                : base.SaveChangesAsync(cancellationToken);
    }
}
