using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02: what the owner and the administrators do with the people already in the org. Who may act on whom (no escalation),
/// the owner out of reach, the seat taken again by a reactivation, the removal that unlinks the org. The real services over
/// EF InMemory; the race for the last seat is proved on PostgreSQL by <c>OrgInvitationsPostgresTests</c> (CI).
/// </summary>
public class OrgTeamServiceTests
{
    private const string OwnerId = "auth0|owner";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<(Guid OrgId, Guid OwnerMemberId)> SeedOrgAsync(
        PlanTier tier = PlanTier.Pro,
        SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync(tier: tier, status: status);
        return (org.Id, (await _kit.ReadMemberAsync(OwnerId))!.Id);
    }

    private async Task<Guid> AddMemberAsync(Guid orgId, string userId, OrgRole role, OrgMemberStatus status = OrgMemberStatus.Active)
    {
        await _kit.SeedMemberAsync(orgId, userId, role, status: status);
        return (await _kit.ReadMemberAsync(userId))!.Id;
    }

    private async Task<T> RunAsync<T>(Func<OrgTeamService, Task<T>> action)
    {
        await using var db = _kit.NewDb();
        return await action(_kit.Team(db));
    }

    private Task RunAsync(Func<OrgTeamService, Task> action) => RunAsync(async team =>
    {
        await action(team);
        return 0;
    });

    // ─── List ───────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_OwnerFirstThenByName_WithTheirAreasStatusAndTheSeats()
    {
        var (orgId, _) = await SeedOrgAsync();
        await _kit.SeedMemberAsync(orgId, "auth0|zeta", OrgRole.Collaborator, ["short-rent", "long-rent"]);
        await _kit.SeedMemberAsync(orgId, "auth0|alfa", OrgRole.Accountant, ["short-rent"]);
        await _kit.SeedMemberAsync(orgId, "auth0|mid", OrgRole.PropertyManager, ["long-rent"], OrgMemberStatus.Deactivated);
        await _kit.SeedInvitationAsync(orgId, "pending@example.com");

        var team = await RunAsync(t => t.ListAsync(orgId));

        Assert.Equal(["auth0|owner", "auth0|alfa", "auth0|mid", "auth0|zeta"], team.Members.Select(m => m.UserId));
        var owner = team.Members[0];
        Assert.Equal((OrgRole.Owner, OrgMemberStatus.Active, "owner@example.com", "Giulia", "Rinaldi"), (owner.Role, owner.Status, owner.Email, owner.FirstName, owner.LastName));
        Assert.Equal(["short-rent"], owner.Areas);
        Assert.Equal(["long-rent", "short-rent"], team.Members.Single(m => m.UserId == "auth0|zeta").Areas);
        var deactivated = team.Members.Single(m => m.UserId == "auth0|mid");
        Assert.Equal(OrgMemberStatus.Deactivated, deactivated.Status);
        Assert.NotNull(deactivated.DeactivatedAt);

        // The owner, the two active members and the pending invitation; the deactivated one holds no seat.
        Assert.Equal((10, 3, 1, 4), (team.Seats.Max, team.Seats.ActiveMembers, team.Seats.PendingInvitations, team.Seats.Used));
        Assert.Equal(6, team.Seats.Available);
    }

    [Fact]
    public async Task ListAsync_NeverShowsThePeopleOfAnotherOrg()
    {
        var (orgId, _) = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await _kit.SeedMemberAsync(other.Id, "auth0|stranger", OrgRole.Admin);

        var team = await RunAsync(t => t.ListAsync(orgId));

        Assert.Equal([OwnerId], team.Members.Select(m => m.UserId));
    }

    // ─── Change of role ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeRoleAsync_OwnerGivesACollaboratorTheAdminRole_AreasKeptAndMembershipsFollow()
    {
        var (orgId, _) = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        var changed = await RunAsync(t => t.ChangeRoleAsync(orgId, memberId, OrgRole.Admin, OwnerId));

        Assert.Equal(OrgRole.Admin, changed.Role);
        Assert.Equal(["short-rent"], changed.Areas);
        Assert.Equal(["account/org_admin", "short-rent/property_manager"], await _kit.MembershipsAsync("auth0|anna"));
        _kit.Cache.Verify(c => c.Invalidate("auth0|anna"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ChangeRoleAsync_AnAdminGivesAnotherRoleToACollaborator_Works()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        var changed = await RunAsync(t => t.ChangeRoleAsync(orgId, memberId, OrgRole.Accountant, "auth0|admin"));

        Assert.Equal(OrgRole.Accountant, changed.Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_AnAdminMakesSomeoneAnAdmin_IsForbidden()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => RunAsync(t => t.ChangeRoleAsync(orgId, memberId, OrgRole.Admin, "auth0|admin")));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Equal(OrgRole.Collaborator, (await _kit.ReadMemberAsync("auth0|anna"))!.Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_AnAdminChangesAnotherAdmin_IsForbidden()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var otherAdmin = await AddMemberAsync(orgId, "auth0|admin2", OrgRole.Admin);

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => RunAsync(t => t.ChangeRoleAsync(orgId, otherAdmin, OrgRole.Collaborator, "auth0|admin")));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Equal(OrgRole.Admin, (await _kit.ReadMemberAsync("auth0|admin2"))!.Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_TheOwnerDemotesAnAdmin_Works()
    {
        var (orgId, _) = await SeedOrgAsync();
        var adminId = await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);

        var changed = await RunAsync(t => t.ChangeRoleAsync(orgId, adminId, OrgRole.PropertyManager, OwnerId));

        Assert.Equal(OrgRole.PropertyManager, changed.Role);
        Assert.Equal(["short-rent/property_manager"], await _kit.MembershipsAsync("auth0|admin"));
    }

    [Fact]
    public async Task ChangeRoleAsync_NobodyBecomesOwner_NotEvenForTheOwner()
    {
        var (orgId, _) = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Admin);

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => RunAsync(t => t.ChangeRoleAsync(orgId, memberId, OrgRole.Owner, OwnerId)));

        Assert.Equal(OrgMembershipErrors.OwnerNotAssignable, error.Code);
    }

    [Fact]
    public async Task ChangeRoleAsync_TheOwnerKeepsItsRole_ForEveryone()
    {
        var (orgId, ownerMemberId) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);

        foreach (var actor in new[] { OwnerId, "auth0|admin" })
        {
            var error = await Assert.ThrowsAsync<DomainConflictException>(
                () => RunAsync(t => t.ChangeRoleAsync(orgId, ownerMemberId, OrgRole.Admin, actor)));
            Assert.Equal(OrgMembershipErrors.LastOwner, error.Code);
        }

        Assert.Equal(OrgRole.Owner, (await _kit.ReadMemberAsync(OwnerId))!.Role);
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public async Task ChangeRoleAsync_ByAPersonWhoDoesNotManageThePeople_IsRefused(OrgRole actorRole)
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|actor", actorRole);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Accountant);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => RunAsync(t => t.ChangeRoleAsync(orgId, memberId, OrgRole.Collaborator, "auth0|actor")));
    }

    [Fact]
    public async Task ChangeRoleAsync_AMemberOfAnotherOrg_IsNotFound()
    {
        var (orgId, _) = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var strangerId = await AddMemberAsync(other.Id, "auth0|stranger", OrgRole.Collaborator);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => RunAsync(t => t.ChangeRoleAsync(orgId, strangerId, OrgRole.Accountant, OwnerId)));

        Assert.Equal(OrgInvitationErrors.MemberNotFound, error.Code);
        Assert.Equal(OrgRole.Collaborator, (await _kit.ReadMemberAsync("auth0|stranger"))!.Role);
    }

    // ─── Deactivate and reactivate ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateAsync_SwitchesTheAccessOffAndFreesTheSeat()
    {
        var (orgId, _) = await SeedOrgAsync(PlanTier.Starter, SubscriptionStatus.None);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await using (var db = _kit.NewDb())
            Assert.False((await _kit.Seats(db).GetUsageAsync(orgId)).CanInvite);

        var deactivated = await RunAsync(t => t.DeactivateAsync(orgId, memberId, OwnerId));

        Assert.Equal(OrgMemberStatus.Deactivated, deactivated.Status);
        Assert.Equal(_kit.Now, deactivated.DeactivatedAt);
        await using var after = _kit.NewDb();
        Assert.True((await _kit.Seats(after).GetUsageAsync(orgId)).CanInvite);
        _kit.Cache.Verify(c => c.Invalidate("auth0|anna"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeactivateAsync_TheOwner_IsRefusedForEveryone()
    {
        var (orgId, ownerMemberId) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);

        foreach (var actor in new[] { OwnerId, "auth0|admin" })
        {
            var error = await Assert.ThrowsAsync<DomainConflictException>(
                () => RunAsync(t => t.DeactivateAsync(orgId, ownerMemberId, actor)));
            Assert.Equal(OrgMembershipErrors.LastOwner, error.Code);
        }

        Assert.Equal(OrgMemberStatus.Active, (await _kit.ReadMemberAsync(OwnerId))!.Status);
    }

    [Fact]
    public async Task DeactivateAsync_AnAdminDeactivatesAnotherAdmin_IsForbiddenButTheOwnerCan()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var otherAdmin = await AddMemberAsync(orgId, "auth0|admin2", OrgRole.Admin);

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => RunAsync(t => t.DeactivateAsync(orgId, otherAdmin, "auth0|admin")));
        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Equal(OrgMemberStatus.Active, (await _kit.ReadMemberAsync("auth0|admin2"))!.Status);

        var done = await RunAsync(t => t.DeactivateAsync(orgId, otherAdmin, OwnerId));
        Assert.Equal(OrgMemberStatus.Deactivated, done.Status);
    }

    [Fact]
    public async Task DeactivateAsync_Twice_IsIdempotent()
    {
        var (orgId, _) = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await RunAsync(t => t.DeactivateAsync(orgId, memberId, OwnerId));
        _kit.Clock.Advance(TimeSpan.FromHours(1));

        var again = await RunAsync(t => t.DeactivateAsync(orgId, memberId, OwnerId));

        Assert.Equal(OrgMemberStatus.Deactivated, again.Status);
        Assert.Equal(_kit.Now.AddHours(-1), again.DeactivatedAt);
    }

    [Fact]
    public async Task ReactivateAsync_WithASeatFree_GivesTheAccessBackAndTakesTheSeat()
    {
        var (orgId, _) = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, OrgMemberStatus.Deactivated);

        var reactivated = await RunAsync(t => t.ReactivateAsync(orgId, memberId, OwnerId));

        Assert.Equal(OrgMemberStatus.Active, reactivated.Status);
        Assert.Null(reactivated.DeactivatedAt);
        await using var db = _kit.NewDb();
        Assert.Equal(2, (await _kit.Seats(db).GetUsageAsync(orgId)).ActiveMembers);
    }

    [Fact]
    public async Task ReactivateAsync_WithTheSeatsTakenMeanwhile_IsAConflictAndTheMemberStaysDeactivated()
    {
        var (orgId, _) = await SeedOrgAsync(PlanTier.Starter, SubscriptionStatus.None);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await RunAsync(t => t.DeactivateAsync(orgId, memberId, OwnerId));
        // The freed seat goes to somebody else.
        await _kit.SeedInvitationAsync(orgId, "newcomer@example.com");

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => RunAsync(t => t.ReactivateAsync(orgId, memberId, OwnerId)));

        Assert.Equal(OrgSeatErrors.LimitReached, error.Code);
        Assert.Equal(OrgMemberStatus.Deactivated, (await _kit.ReadMemberAsync("auth0|anna"))!.Status);
    }

    [Fact]
    public async Task ReactivateAsync_AnActiveMember_IsAnIdempotentNoOpEvenWithTheSeatsFull()
    {
        var (orgId, _) = await SeedOrgAsync(PlanTier.Starter, SubscriptionStatus.None);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        var again = await RunAsync(t => t.ReactivateAsync(orgId, memberId, OwnerId));

        Assert.Equal(OrgMemberStatus.Active, again.Status);
    }

    [Fact]
    public async Task ReactivateAsync_AnAdminReactivatesAnAdmin_IsForbidden()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var otherAdmin = await AddMemberAsync(orgId, "auth0|admin2", OrgRole.Admin, OrgMemberStatus.Deactivated);

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => RunAsync(t => t.ReactivateAsync(orgId, otherAdmin, "auth0|admin")));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
    }

    // ─── Remove ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveAsync_TakesThePersonOutAndUnlinksTheOrgFromTheAccount()
    {
        var (orgId, _) = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Admin);
        Assert.Equal(orgId, (await _kit.ReadUserAsync("auth0|anna")).OrgId);

        await RunAsync(t => t.RemoveAsync(orgId, memberId, OwnerId));

        Assert.Null(await _kit.ReadMemberAsync("auth0|anna"));
        Assert.Empty(await _kit.MembershipsAsync("auth0|anna"));
        var account = await _kit.ReadUserAsync("auth0|anna");
        Assert.Null(account.OrgId);
        Assert.True(account.IsActive);
        _kit.Cache.Verify(c => c.Invalidate("auth0|anna"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RemoveAsync_TheOwner_IsRefusedForEveryone()
    {
        var (orgId, ownerMemberId) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);

        foreach (var actor in new[] { OwnerId, "auth0|admin" })
        {
            var error = await Assert.ThrowsAsync<DomainConflictException>(
                () => RunAsync(t => t.RemoveAsync(orgId, ownerMemberId, actor)));
            Assert.Equal(OrgMembershipErrors.LastOwner, error.Code);
        }

        Assert.NotNull(await _kit.ReadMemberAsync(OwnerId));
        Assert.Equal(orgId, (await _kit.ReadUserAsync(OwnerId)).OrgId);
    }

    [Fact]
    public async Task RemoveAsync_AnAdminRemovesAnAdmin_IsForbidden()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var otherAdmin = await AddMemberAsync(orgId, "auth0|admin2", OrgRole.Admin);

        await Assert.ThrowsAsync<DomainForbiddenException>(() => RunAsync(t => t.RemoveAsync(orgId, otherAdmin, "auth0|admin")));

        Assert.NotNull(await _kit.ReadMemberAsync("auth0|admin2"));
    }

    [Fact]
    public async Task RemoveAsync_FreesTheSeat_AndTheRemovedPersonCanBeInvitedAgain()
    {
        var (orgId, _) = await SeedOrgAsync(PlanTier.Starter, SubscriptionStatus.None);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await RunAsync(t => t.RemoveAsync(orgId, memberId, OwnerId));

        await using var db = _kit.NewDb();
        var sent = await _kit.Invitations(db).CreateAsync(new CreateOrgInvitation(
            orgId, OwnerId, "anna@example.com", "Anna", OrgRole.Collaborator, ["short-rent"]));

        Assert.Equal(OrgInvitationStatus.Pending, sent.Invitation.Status);
    }

    [Fact]
    public async Task RemoveAsync_APersonOfAnotherOrg_IsNotFound()
    {
        var (orgId, _) = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var strangerId = await AddMemberAsync(other.Id, "auth0|stranger", OrgRole.Collaborator);

        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(t => t.RemoveAsync(orgId, strangerId, OwnerId)));

        Assert.NotNull(await _kit.ReadMemberAsync("auth0|stranger"));
    }

    // ─── The membership service itself ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MembershipRemoveAsync_ClearsTheLastUsedContextOfTheOrgButKeepsAStaffOne()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await AddMemberAsync(orgId, "auth0|bruno", OrgRole.Collaborator);
        await using (var db = _kit.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == "auth0|anna")).LastUsedContextKey = "short-rent";
            (await db.Users.SingleAsync(u => u.Id == "auth0|bruno")).LastUsedContextKey = "supplier";
            await db.SaveChangesAsync();
        }

        await using (var db = _kit.NewDb())
        {
            var membership = _kit.Membership(db);
            await membership.RemoveAsync("auth0|anna");
            await membership.RemoveAsync("auth0|bruno");
        }

        Assert.Null((await _kit.ReadUserAsync("auth0|anna")).LastUsedContextKey);
        Assert.Equal("supplier", (await _kit.ReadUserAsync("auth0|bruno")).LastUsedContextKey);
    }

    [Fact]
    public async Task AddMemberAsync_WithAPropertyScope_RecordsIt()
    {
        var (orgId, _) = await SeedOrgAsync();
        await _kit.SeedNewUserAsync("auth0|anna", "anna@example.com");

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).AddMemberAsync("auth0|anna", orgId, OrgRole.Collaborator, ["short-rent"], OwnerId, PropertyScope.Selected);

        Assert.Equal(PropertyScope.Selected, (await _kit.ReadMemberAsync("auth0|anna"))!.PropertyScope);
    }

    [Fact]
    public async Task AbandonEmptyOrgAsync_TheOwnerOfTheOrg_LeavesNoRowAndNoLink()
    {
        var (org, user) = await _kit.SeedPersonWithEmptyOrgAsync("auth0|anna", "anna@example.com");

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).AbandonEmptyOrgAsync(user.Id, org.Id);

        Assert.Null(await _kit.ReadMemberAsync(user.Id));
        Assert.Empty(await _kit.MembershipsAsync(user.Id));
        var account = await _kit.ReadUserAsync(user.Id);
        Assert.Null(account.OrgId);
        Assert.Null(account.LastUsedContextKey);
    }

    [Fact]
    public async Task AbandonEmptyOrgAsync_SomeoneWhoIsNotItsOwner_IsRefused()
    {
        var (orgId, _) = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|anna", OrgRole.Admin);

        await using var db = _kit.NewDb();
        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _kit.Membership(db).AbandonEmptyOrgAsync("auth0|anna", orgId));

        Assert.Equal(OrgMembershipErrors.OtherOrg, error.Code);
        Assert.NotNull(await _kit.ReadMemberAsync("auth0|anna"));
    }
}
