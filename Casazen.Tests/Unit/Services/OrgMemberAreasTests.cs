using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02c: the areas a member works in (<c>PUT /api/orgs/me/members/{id}/areas</c>). The rental memberships are rewritten to the
/// areas asked, the account row of the role stays, at least one area, the owner is not touched and an administrator is in the
/// hands of the owner only; the line of the activity log says the areas before and after; a collaborator «Solo alcuni» keeps its
/// grants and reaches the given properties of the modes it works in. The real services over EF InMemory; the race of this against
/// a change of role and a deactivation is proved on PostgreSQL by <c>OrgTeamCompletionsPostgresTests</c> (CI).
/// </summary>
public class OrgMemberAreasTests
{
    private const string OwnerId = "auth0|owner";
    private const string Both = "both";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<Guid> SeedOrgAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        return org.Id;
    }

    private async Task<Guid> AddMemberAsync(Guid orgId, string userId, OrgRole role, string[]? areas = null)
    {
        await _kit.SeedMemberAsync(orgId, userId, role, areas);
        return (await _kit.ReadMemberAsync(userId))!.Id;
    }

    private async Task<OrgMemberView> SetAreasAsync(Guid orgId, Guid memberId, string actor, params string[] areas)
    {
        await using var db = _kit.NewDb();
        return await _kit.Team(db).SetAreasAsync(orgId, memberId, areas, actor);
    }

    private static string[] AreasOf(string which) => which switch
    {
        "short" => ["short-rent"],
        "long" => ["long-rent"],
        _ => ["short-rent", "long-rent"],
    };

    private async Task<List<OrgActivityEntry>> AreaLinesAsync(Guid orgId) =>
        (await _kit.ReadActivityAsync(orgId)).Where(e => e.Type == OrgActivityType.MemberAreasChanged).ToList();

    // ─── What changes ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAreasAsync_ThePropertyManagerMovesFromShortToLongRent_TheRentalRowFollowsAndTheAccountRowStays()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|pia", OrgRole.PropertyManager, ["short-rent"]);

        var view = await SetAreasAsync(orgId, memberId, OwnerId, "long-rent");

        Assert.Equal(["long-rent"], view.Areas);
        Assert.Equal(OrgRole.PropertyManager, view.Role);
        Assert.Equal(["account/org_manager", "long-rent/property_manager"], await _kit.MembershipsAsync("auth0|pia"));
        // The change is seen on the person's next request: its authorization cache is dropped.
        _kit.Cache.Verify(c => c.Invalidate("auth0|pia"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SetAreasAsync_AddingAnArea_AddsTheRowOfTheRoleForThatArea()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        var view = await SetAreasAsync(orgId, memberId, OwnerId, "short-rent", "long-rent");

        Assert.Equal(["long-rent", "short-rent"], view.Areas);
        Assert.Equal(["long-rent/staff", "short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
    }

    [Fact]
    public async Task SetAreasAsync_TheAccountantKeepsItsInvoicesRow_WhateverTheAreas()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|conti", OrgRole.Accountant, ["short-rent"]);

        await SetAreasAsync(orgId, memberId, OwnerId, "long-rent");

        Assert.Equal(["account/org_accountant", "long-rent/accountant"], await _kit.MembershipsAsync("auth0|conti"));
    }

    [Fact]
    public async Task SetAreasAsync_AnyCaseAndOrderAndRepeats_AreAreasWhatIsNotAnAreaIsIgnored()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        var view = await SetAreasAsync(orgId, memberId, OwnerId, "LONG-RENT", "admin", " short-rent ", "short-rent", "account");

        Assert.Equal(["long-rent", "short-rent"], view.Areas);
        Assert.Equal(["long-rent/staff", "short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
    }

    [Fact]
    public async Task SetAreasAsync_TheSameAreasAgain_ChangeNothingAndSayNothing()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|pia", OrgRole.PropertyManager, ["short-rent"]);

        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        Assert.Equal(["account/org_manager", "short-rent/property_manager"], await _kit.MembershipsAsync("auth0|pia"));
        Assert.Empty(await AreaLinesAsync(orgId));
    }

    [Fact]
    public async Task ChangeRoleAsync_AfterTheAreasWereChanged_KeepsTheNewAreas()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);
        await SetAreasAsync(orgId, memberId, OwnerId, "long-rent");

        await using var db = _kit.NewDb();
        await _kit.Team(db).ChangeRoleAsync(orgId, memberId, OrgRole.PropertyManager, OwnerId);

        Assert.Equal(["account/org_manager", "long-rent/property_manager"], await _kit.MembershipsAsync("auth0|anna"));
    }

    // ─── What is refused ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("admin")]
    [InlineData("account,supplier")]
    [InlineData(", ")]
    public async Task SetAreasAsync_WithNoAreaToWorkIn_Is422_AndNothingChanges(string joinedAreas)
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => SetAreasAsync(orgId, memberId, OwnerId, joinedAreas.Split(',')));

        Assert.Equal(OrgMembershipErrors.AreaRequired, error.Code);
        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
        Assert.Empty(await AreaLinesAsync(orgId));
    }

    [Fact]
    public async Task SetAreasAsync_TheOwner_IsNotTouched_ByItselfOrAnAdministrator()
    {
        var orgId = await SeedOrgAsync();
        var ownerMemberId = (await _kit.ReadMemberAsync(OwnerId))!.Id;
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var before = await _kit.MembershipsAsync(OwnerId);

        foreach (var actor in new[] { OwnerId, "auth0|admin" })
        {
            var error = await Assert.ThrowsAsync<DomainConflictException>(() => SetAreasAsync(orgId, ownerMemberId, actor, "long-rent"));
            Assert.Equal(OrgMembershipErrors.LastOwner, error.Code);
        }

        Assert.Equal(before, await _kit.MembershipsAsync(OwnerId));
    }

    [Fact]
    public async Task SetAreasAsync_AnAdministratorCannotTouchAnotherAdministrator_ButTheOwnerCan()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var otherAdmin = await AddMemberAsync(orgId, "auth0|admin2", OrgRole.Admin, ["short-rent"]);

        var error = await Assert.ThrowsAsync<DomainForbiddenException>(() => SetAreasAsync(orgId, otherAdmin, "auth0|admin", "long-rent"));

        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        Assert.Equal(["account/org_admin", "short-rent/property_manager"], await _kit.MembershipsAsync("auth0|admin2"));

        await SetAreasAsync(orgId, otherAdmin, OwnerId, "long-rent");
        Assert.Equal(["account/org_admin", "long-rent/property_manager"], await _kit.MembershipsAsync("auth0|admin2"));
    }

    [Fact]
    public async Task SetAreasAsync_AnAdministrator_ChangesTheAreasOfAnyoneBelowIt()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        var view = await SetAreasAsync(orgId, memberId, "auth0|admin", "long-rent");

        Assert.Equal(["long-rent"], view.Areas);
    }

    [Fact]
    public async Task SetAreasAsync_ACallerWhoDoesNotManageThePeople_IsRefused()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|pia", OrgRole.PropertyManager);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SetAreasAsync(orgId, memberId, "auth0|pia", "long-rent"));

        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
    }

    [Fact]
    public async Task SetAreasAsync_AMemberOfAnotherOrgOrOneThatDoesNotExist_IsNotFound()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var stranger = await AddMemberAsync(other.Id, "auth0|stranger", OrgRole.Collaborator, ["short-rent"]);

        foreach (var memberId in new[] { stranger, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<NotFoundException>(() => SetAreasAsync(orgId, memberId, OwnerId, "long-rent"));
            Assert.Equal(OrgInvitationErrors.MemberNotFound, error.Code);
        }

        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync("auth0|stranger"));
    }

    // ─── The activity log ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetAreasAsync_WritesOneLine_WithWhoWhomAndTheAreasBeforeAndAfter()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent", "long-rent");

        var line = Assert.Single(await AreaLinesAsync(orgId));
        Assert.Equal((OwnerId, "auth0|anna", OrgActivityArea.Account, OrgActivitySubjectType.Member),
            (line.ActorUserId, line.SubjectId, line.Area, line.SubjectType));
        var details = OrgActivityDetails.Parse(line.DetailsJson);
        Assert.Equal(("short-rent", "long-rent.short-rent"), (details["fromAreas"], details["toAreas"]));
        Assert.False(details.ContainsKey("released"));
    }

    // ─── «Solo alcuni»: the grants stay, the reach follows the areas ────────────────────────────────────

    private async Task<(Guid MemberId, Guid ShortProperty, Guid LongProperty)> LimitedCollaboratorAsync(Guid orgId)
    {
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent", "long-rent"]);
        var trullo = await _kit.SeedPropertyAsync(orgId, "Trullo", RentalMode.Short);
        var bilocale = await _kit.SeedPropertyAsync(orgId, "Bilocale", RentalMode.Long);
        await using var db = _kit.NewDb();
        await _kit.PropertyAccess(db).SetAsync(orgId, memberId, OwnerId, PropertyScope.Selected, [trullo, bilocale]);
        return (memberId, trullo, bilocale);
    }

    private async Task<UserAuthorizationSnapshot> SnapshotAsync(string userId)
    {
        await using var db = _kit.NewDb();
        var store = new UserAuthorizationSnapshotStore(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            new HttpContextAccessor(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Authorization:UserCacheSeconds"] = "0" }).Build());
        return await store.GetAsync(userId);
    }

    private async Task<List<Guid>> ReachedAsync(Guid orgId, string userId)
    {
        var snapshot = await SnapshotAsync(userId);
        var scope = HostScopeResolver.Decide(snapshot, userId, new HashSet<string>(), orgId)
                    ?? throw new InvalidOperationException("The member has no scope.");

        await using var db = _kit.NewDb();
        return await db.Properties.AsNoTracking().Where(p => p.OrgId == orgId).InScope(scope).Select(p => p.Id).OrderBy(id => id).ToListAsync();
    }

    [Fact]
    public async Task SetAreasAsync_ALimitedCollaboratorKeepsItsGrants_AndReachesTheGivenPropertiesOfTheAreasItWorksIn()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, bilocale) = await LimitedCollaboratorAsync(orgId);
        var both = new[] { trullo, bilocale }.Order().ToList();
        Assert.Equal(both, await ReachedAsync(orgId, "auth0|anna"));

        // Short-term only: the long-term grant is kept (the area can be given back) and is not reached.
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");
        Assert.Equal(both, await _kit.ReadGrantsAsync("auth0|anna"));
        Assert.Equal([trullo], await ReachedAsync(orgId, "auth0|anna"));
        var snapshot = await SnapshotAsync("auth0|anna");
        Assert.Equal(RentalMode.Short, snapshot.OrgMember!.GrantedMode);
        Assert.Equal([trullo], snapshot.OrgMember.GrantedPropertyIds!.ToArray());

        // Long-term only: the other way round.
        await SetAreasAsync(orgId, memberId, OwnerId, "long-rent");
        Assert.Equal([bilocale], await ReachedAsync(orgId, "auth0|anna"));

        // Both areas again: the grants come back with nothing to redo.
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent", "long-rent");
        Assert.Equal(both, await ReachedAsync(orgId, "auth0|anna"));
        Assert.Null((await SnapshotAsync("auth0|anna")).OrgMember!.GrantedMode);
        Assert.Equal(both, await _kit.ReadGrantsAsync("auth0|anna"));
    }

    [Fact]
    public async Task SetAreasAsync_NarrowingALimitedCollaborator_TakesTheResponsibilityOffThePropertiesItNoLongerReaches()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, bilocale) = await LimitedCollaboratorAsync(orgId);
        await using (var db = _kit.NewDb())
        {
            foreach (var property in await db.Properties.IgnoreQueryFilters().Where(p => p.OrgId == orgId).ToListAsync())
                property.ResponsibleUserId = "auth0|anna";
            await db.SaveChangesAsync();
        }

        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        await using var verify = _kit.NewDb();
        var responsible = await verify.Properties.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrgId == orgId)
            .ToDictionaryAsync(p => p.Id, p => p.ResponsibleUserId);
        // The long-term property lost its person in charge, the short-term one did not; the grant rows are all still there.
        Assert.Equal("auth0|anna", responsible[trullo]);
        Assert.Null(responsible[bilocale]);
        Assert.Equal(2, (await _kit.ReadGrantsAsync("auth0|anna")).Count);
        var details = OrgActivityDetails.Parse(Assert.Single(await AreaLinesAsync(orgId)).DetailsJson);
        Assert.Equal("1", details["released"]);
    }

    [Fact]
    public async Task SetAreasAsync_WideningALimitedCollaborator_ReleasesNothing()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, _) = await LimitedCollaboratorAsync(orgId);
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");
        await using (var db = _kit.NewDb())
        {
            (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == trullo)).ResponsibleUserId = "auth0|anna";
            await db.SaveChangesAsync();
        }

        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent", "long-rent");

        await using var verify = _kit.NewDb();
        Assert.Equal("auth0|anna", (await verify.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == trullo)).ResponsibleUserId);
    }

    [Fact]
    public async Task SetAreasAsync_APropertyManagerInChargeOfAPropertyKeepsIt_ItReachesTheWholeOrg()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|pia", OrgRole.PropertyManager, ["short-rent", "long-rent"]);
        var bilocale = await _kit.SeedPropertyAsync(orgId, "Bilocale", RentalMode.Long);
        await using (var db = _kit.NewDb())
        {
            (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == bilocale)).ResponsibleUserId = "auth0|pia";
            await db.SaveChangesAsync();
        }

        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        await using var verify = _kit.NewDb();
        Assert.Equal("auth0|pia", (await verify.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == bilocale)).ResponsibleUserId);
    }

    [Fact]
    public async Task SetResponsibleAsync_ALimitedCollaborator_CannotBePutInChargeOfAGivenPropertyOfAModeItDoesNotWorkIn()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, bilocale) = await LimitedCollaboratorAsync(orgId);
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        await using var db = _kit.NewDb();
        var access = _kit.PropertyAccess(db);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => access.SetResponsibleAsync(orgId, bilocale, "auth0|anna"));
        Assert.Equal(PropertyResponsibleErrors.Invalid, error.Code);
        // The one it does reach is fine.
        await access.SetResponsibleAsync(orgId, trullo, "auth0|anna");
    }

    // ─── Found by the review of the change ──────────────────────────────────────────────────────────────

    private async Task NameInChargeAsync(Guid propertyId, string userId)
    {
        await using var db = _kit.NewDb();
        (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId)).ResponsibleUserId = userId;
        await db.SaveChangesAsync();
    }

    private async Task<string?> ResponsibleOfAsync(Guid propertyId)
    {
        await using var db = _kit.NewDb();
        return (await db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId)).ResponsibleUserId;
    }

    [Fact]
    public void AreasCode_NoAreasAtAll_IsACodeTheLogAccepts_AndTheOthersAreSortedAndJoined()
    {
        // An empty value is not a code: the log refuses it, and the change that repairs a member with no area could not be written.
        Assert.Equal("none", OrgActivityDetails.AreasCode([]));
        Assert.True(OrgActivityCatalog.IsSafeDetailValue(OrgActivityDetails.AreasCode([])));
        Assert.Equal("long-rent.short-rent", OrgActivityDetails.AreasCode(["short-rent", "LONG-RENT", "short-rent"]));
        Assert.Equal("short-rent", OrgActivityDetails.AreasCode([" Short-Rent "]));
    }

    [Fact]
    public async Task SetAreasAsync_AMemberWithNoRentalMembership_IsRepaired_AndTheLineSaysItHadNoAreas()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator, ["short-rent"]);

        // A drift the reconcile reports as member_without_host_membership: the member row is there and its rental rows are not.
        await using (var db = _kit.NewDb())
        {
            db.UserContextMemberships.RemoveRange(db.UserContextMemberships.Where(m => m.UserId == "auth0|anna"));
            await db.SaveChangesAsync();
        }

        Assert.Empty(await _kit.MembershipsAsync("auth0|anna"));

        var view = await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        Assert.Equal(["short-rent"], view.Areas);
        Assert.Equal(["short-rent/staff"], await _kit.MembershipsAsync("auth0|anna"));
        var details = OrgActivityDetails.Parse(Assert.Single(await AreaLinesAsync(orgId)).DetailsJson);
        Assert.Equal(("none", "short-rent"), (details["fromAreas"], details["toAreas"]));
    }

    [Fact]
    public async Task SetAreasAsync_TheSameAreasButAPersonInChargeIsReleased_StillWritesItsLine()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, _, bilocale) = await LimitedCollaboratorAsync(orgId);
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");
        // A stale name on a long-term property (it changed mode, or a row written elsewhere): nothing else is wrong.
        await NameInChargeAsync(bilocale, "auth0|anna");

        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        // The name is gone, and a change of state has its line even though the areas are the same: with the count that says what happened.
        Assert.Null(await ResponsibleOfAsync(bilocale));
        var released = Assert.Single(
            await AreaLinesAsync(orgId),
            line => OrgActivityDetails.Parse(line.DetailsJson).ContainsKey("released"));
        var details = OrgActivityDetails.Parse(released.DetailsJson);
        Assert.Equal(("short-rent", "short-rent", "1"), (details["fromAreas"], details["toAreas"], details["released"]));
    }

    [Fact]
    public async Task SetAsync_ALimitedCollaboratorOfOneArea_DoesNotStayInChargeOfAGivenPropertyOfTheOtherMode()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, bilocale) = await LimitedCollaboratorAsync(orgId);
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");
        await NameInChargeAsync(bilocale, "auth0|anna");
        await NameInChargeAsync(trullo, "auth0|anna");

        // The owner saves the access with both properties still given: the long-term one is not reached, so the name goes.
        await using (var db = _kit.NewDb())
            await _kit.PropertyAccess(db).SetAsync(orgId, memberId, OwnerId, PropertyScope.Selected, [trullo, bilocale]);

        Assert.Equal("auth0|anna", await ResponsibleOfAsync(trullo));
        Assert.Null(await ResponsibleOfAsync(bilocale));
        // The grants are exactly what was sent: nothing was taken away.
        Assert.Equal(new[] { trullo, bilocale }.Order(), (await _kit.ReadGrantsAsync("auth0|anna")).Order());
    }

    [Fact]
    public async Task GetAsync_PeopleWithAccess_CountsOnlyThePeopleWhoWorkInAnAreaOfThePropertysMode_AndGrantedStaysTheStoredGrant()
    {
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, bilocale) = await LimitedCollaboratorAsync(orgId);

        await using (var both = _kit.NewDb())
        {
            var view = await _kit.PropertyAccess(both).GetAsync(orgId, memberId);
            // The owner reaches everything and Anna works in both areas: two people each.
            Assert.All(view.Properties, p => Assert.Equal(2, p.PeopleWithAccess));
        }

        // Anna gives up long-term rentals: she keeps the grant on the bilocale and no longer reaches it.
        await SetAreasAsync(orgId, memberId, OwnerId, "short-rent");

        await using var db = _kit.NewDb();
        var items = (await _kit.PropertyAccess(db).GetAsync(orgId, memberId)).Properties.ToDictionary(p => p.PropertyId);
        Assert.Equal(2, items[trullo].PeopleWithAccess);
        Assert.Equal(1, items[bilocale].PeopleWithAccess);
        // What an editor sends back is the stored grant: saving the page as it is must not take the bilocale away.
        Assert.True(items[bilocale].Granted);
    }

    [Theory]
    [InlineData("short", RentalMode.Short, true)]
    [InlineData("short", RentalMode.Long, false)]
    [InlineData("long", RentalMode.Long, true)]
    [InlineData("long", RentalMode.Short, false)]
    [InlineData(Both, RentalMode.Short, true)]
    [InlineData(Both, RentalMode.Long, true)]
    public void SingleModeOf_EachCombinationOfAreas_NarrowsOnlyAMemberWhoWorksInOneArea(string areas, RentalMode propertyMode, bool reached)
    {
        var mode = OrgRoleCatalog.SingleModeOf(AreasOf(areas));

        Assert.Equal(reached, mode is not { } only || only == propertyMode);
    }

    [Fact]
    public void SingleModeOf_NoAreaKnown_NarrowsNothing_AndWhatIsNotAnAreaIsIgnored()
    {
        Assert.Null(OrgRoleCatalog.SingleModeOf([]));
        Assert.Null(OrgRoleCatalog.SingleModeOf(["admin", "account"]));
        Assert.Equal(RentalMode.Long, OrgRoleCatalog.SingleModeOf(["LONG-RENT", "admin", "long-rent"]));
    }

    [Fact]
    public async Task Decide_ALimitedCollaboratorWithNoRentalMembership_IsNotNarrowed()
    {
        // A drift the reconcile reports (no area known): the grants are not narrowed, and the member holds no context permission
        // either, so the permission layer is the one that refuses.
        var orgId = await SeedOrgAsync();
        var (memberId, trullo, bilocale) = await LimitedCollaboratorAsync(orgId);
        await using (var db = _kit.NewDb())
        {
            db.UserContextMemberships.RemoveRange(db.UserContextMemberships.Where(m => m.UserId == "auth0|anna"));
            await db.SaveChangesAsync();
        }

        Assert.Equal(new[] { trullo, bilocale }.Order(), await ReachedAsync(orgId, "auth0|anna"));
        Assert.Null((await SnapshotAsync("auth0|anna")).OrgMember!.GrantedMode);
        Assert.NotEqual(Guid.Empty, memberId);
    }
}
