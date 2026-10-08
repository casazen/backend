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
/// AM-01: the one service that writes the org membership. Each write saves the org member row and the memberships that
/// project its role together (one <c>SaveChanges</c>, so all or nothing), and invalidates the user's authorization cache.
/// On PostgreSQL the concurrency and the unique index are proved by <c>OrgMembershipPostgresTests</c>.
/// </summary>
public class OrgMembershipServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 10, 30, 0, TimeSpan.Zero);

    private readonly Mock<IUserAuthorizationCache> _cache = new();
    private readonly string _database = Guid.NewGuid().ToString();

    private OrgMembershipService NewService(AppDbContext db) =>
        new(db, _cache.Object, NullLogger<OrgMembershipService>.Instance, new FixedTimeProvider(Now));

    // ─── EnsureOwnerAsync ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureOwnerAsync_NewOwner_WritesTheMemberAndTheAccountMembershipTogether()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddMembership(db, "auth0|owner", "short-rent", "property_owner");
        await db.SaveChangesAsync();

        var member = await NewService(db).EnsureOwnerAsync("auth0|owner", org.Id);

        await using var verify = NewDb(_database);
        var stored = await verify.OrgMembers.AsNoTracking().SingleAsync(m => m.UserId == "auth0|owner");
        Assert.Equal(member.Id, stored.Id);
        Assert.Equal(org.Id, stored.OrgId);
        Assert.Equal(OrgRole.Owner, stored.Role);
        Assert.Equal(OrgMemberStatus.Active, stored.Status);
        Assert.Equal(PropertyScope.All, stored.PropertyScope);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAt);
        Assert.Null(stored.CreatedByUserId);
        Assert.Null(stored.DeactivatedAt);
        // The owner's own rental membership is the onboarding's: it is still there, untouched.
        Assert.Equal(["account/org_owner", "short-rent/property_owner"], await MembershipsOfAsync(verify, "auth0|owner"));
        _cache.Verify(c => c.Invalidate("auth0|owner"), Times.Once);
    }

    [Fact]
    public async Task EnsureOwnerAsync_AccountRoleRowMissing_StillWritesTheMemberAndTheReconcileAddsTheMembershipLater()
    {
        // A database without the seeded account role (never production: the migration runs at every startup) must not
        // stop a new customer from onboarding. The member row, the source of truth, is written; the projection is
        // skipped with a warning and the reconcile command writes it once the role exists. (AddMemberAsync, by
        // contrast, refuses: see AddMemberAsync_ARoleRowIsMissing_SavesNothingNotEvenTheMember.)
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddMembership(db, "auth0|owner", "short-rent", "property_owner");
        var accountOwner = await db.Roles.SingleAsync(r => r.ContextKey == "account" && r.RoleKey == "org_owner");
        db.Roles.Remove(accountOwner);
        await db.SaveChangesAsync();

        var member = await NewService(db).EnsureOwnerAsync("auth0|owner", org.Id);

        await using var verify = NewDb(_database);
        Assert.Equal(member.Id, (await verify.OrgMembers.SingleAsync()).Id);
        Assert.Equal(["short-rent/property_owner"], await MembershipsOfAsync(verify, "auth0|owner"));

        verify.Roles.Add(new Role { Id = accountOwner.Id, ContextKey = "account", RoleKey = "org_owner" });
        await verify.SaveChangesAsync();
        var report = await NewService(verify).ReconcileAsync(dryRun: false);

        Assert.Contains(report.Fixes, f => f.Code == OrgMembershipReconcileCodes.AccountMembershipAdded && f.UserId == "auth0|owner");
        Assert.Equal(["account/org_owner", "short-rent/property_owner"], await MembershipsOfAsync(verify, "auth0|owner"));
    }

    [Fact]
    public async Task EnsureOwnerAsync_CalledAgain_IsIdempotent()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        await db.SaveChangesAsync();
        var service = NewService(db);

        var first = await service.EnsureOwnerAsync("auth0|owner", org.Id);
        var second = await service.EnsureOwnerAsync("auth0|owner", org.Id);

        Assert.Equal(first.Id, second.Id);
        await using var verify = NewDb(_database);
        Assert.Equal(1, await verify.OrgMembers.CountAsync());
        Assert.Equal(["account/org_owner"], await MembershipsOfAsync(verify, "auth0|owner"));
    }

    [Fact]
    public async Task EnsureOwnerAsync_UserOfAnotherOrg_IsRefusedAndWritesNothing()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        var other = AddOrg(db);
        AddUser(db, "auth0|elsewhere", other.Id, UserRole.PropertyOwner);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => NewService(db).EnsureOwnerAsync("auth0|elsewhere", org.Id));

        Assert.Equal(OrgMembershipErrors.OtherOrg, error.Code);
        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
        Assert.Empty(await verify.UserContextMemberships.ToListAsync());
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task EnsureOwnerAsync_UnknownUser_IsNotFound()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => NewService(db).EnsureOwnerAsync("auth0|nobody", org.Id));
    }

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.Collaborator)]
    public async Task EnsureOwnerAsync_UserThatIsAlreadyAnotherRoleOfTheOrg_IsRefused(OrgRole existing)
    {
        // The onboarding of a member never makes it the owner (AM-00, S2): the org member row is the source of truth.
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        AddMember(db, "auth0|member", org.Id, existing);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => NewService(db).EnsureOwnerAsync("auth0|member", org.Id));

        Assert.Equal(OrgMembershipErrors.AlreadyMember, error.Code);
        await using var verify = NewDb(_database);
        Assert.Equal(existing, (await verify.OrgMembers.SingleAsync()).Role);
    }

    // ─── AddMemberAsync ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddMemberAsync_Admin_WritesTheMemberAndItsProjectionInOneSave()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|inviter", org.Id, UserRole.PropertyOwner);
        AddUser(db, "auth0|admin", orgId: null);
        await db.SaveChangesAsync();

        var member = await NewService(db).AddMemberAsync(
            "auth0|admin", org.Id, OrgRole.Admin, ["short-rent", "long-rent"], "auth0|inviter");

        await using var verify = NewDb(_database);
        var stored = await verify.OrgMembers.AsNoTracking().SingleAsync(m => m.UserId == "auth0|admin");
        Assert.Equal(member.Id, stored.Id);
        Assert.Equal(org.Id, stored.OrgId);
        Assert.Equal(OrgRole.Admin, stored.Role);
        Assert.Equal(OrgMemberStatus.Active, stored.Status);
        Assert.Equal(PropertyScope.All, stored.PropertyScope);
        Assert.Equal("auth0|inviter", stored.CreatedByUserId);
        Assert.Equal(Now.UtcDateTime, stored.CreatedAt);
        Assert.Equal(
            ["account/org_admin", "long-rent/property_manager", "short-rent/property_manager"],
            await MembershipsOfAsync(verify, "auth0|admin"));
        // The user had no org: it now belongs to this one.
        Assert.Equal(org.Id, (await verify.Users.AsNoTracking().SingleAsync(u => u.Id == "auth0|admin")).OrgId);
        _cache.Verify(c => c.Invalidate("auth0|admin"), Times.Once);
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager, new[] { "short-rent" }, new[] { "short-rent/property_manager" })]
    [InlineData(OrgRole.Collaborator, new[] { "long-rent" }, new[] { "long-rent/staff" })]
    [InlineData(OrgRole.Collaborator, new[] { "short-rent", "long-rent" }, new[] { "long-rent/staff", "short-rent/staff" })]
    [InlineData(OrgRole.Accountant, new[] { "short-rent" }, new[] { "account/org_accountant", "short-rent/accountant" })]
    public async Task AddMemberAsync_RoleAndAreas_WriteExactlyTheMembershipsOfTheCatalog(
        OrgRole role, string[] areas, string[] expected)
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();

        await NewService(db).AddMemberAsync("auth0|member", org.Id, role, areas, createdByUserId: null);

        await using var verify = NewDb(_database);
        Assert.Equal(expected, await MembershipsOfAsync(verify, "auth0|member"));
    }

    [Fact]
    public async Task AddMemberAsync_OwnerRole_IsNotAssignable()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(db).AddMemberAsync("auth0|member", org.Id, OrgRole.Owner, ["short-rent"], null));

        Assert.Equal(OrgMembershipErrors.OwnerNotAssignable, error.Code);
        Assert.Equal("OrgMemberOwnerNotAssignable", error.MessageKey);
    }

    public static TheoryData<string[]> NoRentalAreas() => new()
    {
        Array.Empty<string>(),
        new[] { "account" },
        new[] { "admin", "supplier" },
    };

    [Theory]
    [MemberData(nameof(NoRentalAreas))]
    public async Task AddMemberAsync_NoRentalArea_IsRefused(string[] areas)
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => NewService(db).AddMemberAsync("auth0|member", org.Id, OrgRole.PropertyManager, areas, null));

        Assert.Equal(OrgMembershipErrors.AreaRequired, error.Code);
        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
    }

    [Fact]
    public async Task AddMemberAsync_UserThatIsAlreadyAMember_IsRefused()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        AddMember(db, "auth0|member", org.Id, OrgRole.Collaborator);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => NewService(db).AddMemberAsync("auth0|member", org.Id, OrgRole.Admin, ["short-rent"], null));

        Assert.Equal(OrgMembershipErrors.AlreadyMember, error.Code);
        await using var verify = NewDb(_database);
        Assert.Equal(OrgRole.Collaborator, (await verify.OrgMembers.SingleAsync()).Role);
        Assert.Empty(await verify.UserContextMemberships.ToListAsync());
    }

    [Fact]
    public async Task AddMemberAsync_UserOfAnotherOrg_IsRefused()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        var other = AddOrg(db);
        AddUser(db, "auth0|elsewhere", other.Id);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => NewService(db).AddMemberAsync("auth0|elsewhere", org.Id, OrgRole.Collaborator, ["short-rent"], null));

        Assert.Equal(OrgMembershipErrors.OtherOrg, error.Code);
        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
        Assert.Equal(other.Id, (await verify.Users.SingleAsync()).OrgId);
    }

    [Fact]
    public async Task AddMemberAsync_UnknownUserOrNotAHostOrg_IsNotFound()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        var supplierOrg = AddOrg(db, OrgType.Supplier);
        AddUser(db, "auth0|member", orgId: null);
        await db.SaveChangesAsync();
        var service = NewService(db);

        await Assert.ThrowsAsync<NotFoundException>(
            () => service.AddMemberAsync("auth0|nobody", org.Id, OrgRole.Admin, ["short-rent"], null));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.AddMemberAsync("auth0|member", supplierOrg.Id, OrgRole.Admin, ["short-rent"], null));
        await Assert.ThrowsAsync<NotFoundException>(
            () => service.AddMemberAsync("auth0|member", Guid.NewGuid(), OrgRole.Admin, ["short-rent"], null));
    }

    [Fact]
    public async Task AddMemberAsync_ARoleRowIsMissing_SavesNothingNotEvenTheMember()
    {
        // All or nothing: the staff role of short-rent does not exist, the projection cannot be written, so the member
        // row, staged first, is not saved either, and the user is not moved into the org.
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", orgId: null);
        db.Roles.Remove(await db.Roles.SingleAsync(r => r.ContextKey == "short-rent" && r.RoleKey == "staff"));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(db).AddMemberAsync("auth0|member", org.Id, OrgRole.Collaborator, ["short-rent"], null));

        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
        Assert.Empty(await verify.UserContextMemberships.ToListAsync());
        Assert.Null((await verify.Users.SingleAsync()).OrgId);
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task AddMemberAsync_KeepsTheMembershipsOfOtherContexts()
    {
        // A supplier link or a staff membership of the same account is not an org matter: the add never touches it.
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", orgId: null);
        AddMembership(db, "auth0|member", "admin", "platform_admin");
        await db.SaveChangesAsync();

        await NewService(db).AddMemberAsync("auth0|member", org.Id, OrgRole.Collaborator, ["short-rent"], null);

        await using var verify = NewDb(_database);
        Assert.Equal(["admin/platform_admin", "short-rent/staff"], await MembershipsOfAsync(verify, "auth0|member"));
    }

    // ─── ChangeRoleAsync ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeRoleAsync_AdminToCollaborator_RepointsTheRentalRowsAndDropsTheAccountRow()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.AddMemberAsync("auth0|member", org.Id, OrgRole.Admin, ["short-rent", "long-rent"], null);
        _cache.Invocations.Clear();

        var changed = await service.ChangeRoleAsync("auth0|member", OrgRole.Collaborator);

        Assert.Equal(OrgRole.Collaborator, changed.Role);
        await using var verify = NewDb(_database);
        Assert.Equal(OrgRole.Collaborator, (await verify.OrgMembers.SingleAsync()).Role);
        Assert.Equal(["long-rent/staff", "short-rent/staff"], await MembershipsOfAsync(verify, "auth0|member"));
        _cache.Verify(c => c.Invalidate("auth0|member"), Times.Once);
    }

    [Fact]
    public async Task ChangeRoleAsync_CollaboratorToAdmin_AddsTheAccountRowAndKeepsTheAreas()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.AddMemberAsync("auth0|member", org.Id, OrgRole.Collaborator, ["long-rent"], null);

        await service.ChangeRoleAsync("auth0|member", OrgRole.Admin);

        await using var verify = NewDb(_database);
        // Only long-rent: the areas are the contexts the person already worked in.
        Assert.Equal(["account/org_admin", "long-rent/property_manager"], await MembershipsOfAsync(verify, "auth0|member"));
    }

    [Fact]
    public async Task ChangeRoleAsync_OwnerRoleNotAssignable_AndTheOwnerKeepsItsRole()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.EnsureOwnerAsync("auth0|owner", org.Id);
        await service.AddMemberAsync("auth0|member", org.Id, OrgRole.Collaborator, ["short-rent"], null);

        var toOwner = await Assert.ThrowsAsync<DomainRuleException>(() => service.ChangeRoleAsync("auth0|member", OrgRole.Owner));
        var ownerDemoted = await Assert.ThrowsAsync<DomainConflictException>(() => service.ChangeRoleAsync("auth0|owner", OrgRole.Admin));

        Assert.Equal(OrgMembershipErrors.OwnerNotAssignable, toOwner.Code);
        Assert.Equal(OrgMembershipErrors.LastOwner, ownerDemoted.Code);
        await using var verify = NewDb(_database);
        Assert.Equal(OrgRole.Owner, (await verify.OrgMembers.SingleAsync(m => m.UserId == "auth0|owner")).Role);
        Assert.Equal(OrgRole.Collaborator, (await verify.OrgMembers.SingleAsync(m => m.UserId == "auth0|member")).Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_UserThatIsNotAMember_IsNotFound()
    {
        await using var db = NewDb(_database);

        await Assert.ThrowsAsync<NotFoundException>(() => NewService(db).ChangeRoleAsync("auth0|nobody", OrgRole.Admin));
    }

    // ─── DeactivateAsync / ReactivateAsync ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAsync_Member_SetsTheStatusAndKeepsTheMembershipsForTheReactivation()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.AddMemberAsync("auth0|member", org.Id, OrgRole.Admin, ["short-rent"], null);
        _cache.Invocations.Clear();

        var deactivated = await service.DeactivateAsync("auth0|member");

        Assert.Equal(OrgMemberStatus.Deactivated, deactivated.Status);
        await using var verify = NewDb(_database);
        var stored = await verify.OrgMembers.SingleAsync();
        Assert.Equal(OrgMemberStatus.Deactivated, stored.Status);
        Assert.Equal(Now.UtcDateTime, stored.DeactivatedAt);
        Assert.Equal(["account/org_admin", "short-rent/property_manager"], await MembershipsOfAsync(verify, "auth0|member"));
        // The user's account is not touched: only the org membership is.
        Assert.True((await verify.Users.SingleAsync()).IsActive);
        _cache.Verify(c => c.Invalidate("auth0|member"), Times.Once);
    }

    [Fact]
    public async Task DeactivateAsync_AlreadyDeactivated_ChangesNothing()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();
        await NewService(db).AddMemberAsync("auth0|member", org.Id, OrgRole.Collaborator, ["short-rent"], null);
        await NewService(db).DeactivateAsync("auth0|member");
        _cache.Invocations.Clear();

        // Later, with another clock: the first deactivation date is kept.
        var later = new OrgMembershipService(
            db, _cache.Object, NullLogger<OrgMembershipService>.Instance, new FixedTimeProvider(Now.AddDays(3)));
        await later.DeactivateAsync("auth0|member");

        await using var verify = NewDb(_database);
        Assert.Equal(Now.UtcDateTime, (await verify.OrgMembers.SingleAsync()).DeactivatedAt);
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReactivateAsync_DeactivatedMember_GivesTheAccessBack()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.AddMemberAsync("auth0|member", org.Id, OrgRole.Collaborator, ["short-rent"], null);
        await service.DeactivateAsync("auth0|member");
        _cache.Invocations.Clear();

        var reactivated = await service.ReactivateAsync("auth0|member");

        Assert.Equal(OrgMemberStatus.Active, reactivated.Status);
        await using var verify = NewDb(_database);
        var stored = await verify.OrgMembers.SingleAsync();
        Assert.Equal(OrgMemberStatus.Active, stored.Status);
        Assert.Null(stored.DeactivatedAt);
        _cache.Verify(c => c.Invalidate("auth0|member"), Times.Once);
    }

    [Fact]
    public async Task ReactivateAsync_ActiveMember_ChangesNothing()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        AddMember(db, "auth0|member", org.Id, OrgRole.Collaborator);
        await db.SaveChangesAsync();

        await NewService(db).ReactivateAsync("auth0|member");

        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeactivateAsync_TheOwner_IsRefusedBecauseAnOrgKeepsItsOwner()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.EnsureOwnerAsync("auth0|owner", org.Id);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => service.DeactivateAsync("auth0|owner"));

        Assert.Equal("org_last_owner", error.Code);
        Assert.Equal("OrgLastOwner", error.MessageKey);
        await using var verify = NewDb(_database);
        Assert.Equal(OrgMemberStatus.Active, (await verify.OrgMembers.SingleAsync()).Status);
    }

    [Fact]
    public async Task DeactivateAndReactivate_UserThatIsNotAMember_AreNotFound()
    {
        await using var db = NewDb(_database);
        var service = NewService(db);

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeactivateAsync("auth0|nobody"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ReactivateAsync("auth0|nobody"));
    }

    // ─── RemoveAsync ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveAsync_Member_DeletesTheRowAndEveryMembershipTheOrgGaveButNotTheStaffOnes()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|member", org.Id);
        AddMembership(db, "auth0|member", "admin", "platform_admin");
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.AddMemberAsync("auth0|member", org.Id, OrgRole.Accountant, ["short-rent", "long-rent"], null);
        _cache.Invocations.Clear();

        await service.RemoveAsync("auth0|member");

        await using var verify = NewDb(_database);
        Assert.Empty(await verify.OrgMembers.ToListAsync());
        Assert.Equal(["admin/platform_admin"], await MembershipsOfAsync(verify, "auth0|member"));
        _cache.Verify(c => c.Invalidate("auth0|member"), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_TheOwner_IsRefused()
    {
        await using var db = NewDb(_database);
        var org = AddOrg(db);
        AddUser(db, "auth0|owner", org.Id, UserRole.PropertyOwner);
        await db.SaveChangesAsync();
        var service = NewService(db);
        await service.EnsureOwnerAsync("auth0|owner", org.Id);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => service.RemoveAsync("auth0|owner"));

        Assert.Equal(OrgMembershipErrors.LastOwner, error.Code);
        await using var verify = NewDb(_database);
        Assert.Single(await verify.OrgMembers.ToListAsync());
        Assert.Equal(["account/org_owner"], await MembershipsOfAsync(verify, "auth0|owner"));
    }

    [Fact]
    public async Task RemoveAsync_UserThatIsNotAMember_IsNotFound()
    {
        await using var db = NewDb(_database);

        await Assert.ThrowsAsync<NotFoundException>(() => NewService(db).RemoveAsync("auth0|nobody"));
    }
}
