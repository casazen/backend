using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03: which properties a member of the org reaches, and who is in charge of a property. The real service over EF InMemory
/// (the lock and the transaction are PostgreSQL only and are proved by <c>HostScopePostgresTests</c> in CI): who may change
/// the access of whom, that only a collaborator can be limited, that a property of another org can never be granted, the
/// difference written and not the whole set, the cache of the member invalidated once, and the count of people with access.
/// </summary>
public class OrgPropertyAccessServiceTests
{
    private const string OwnerId = "auth0|owner";

    private readonly OrgInvitationTestKit _kit = new();

    private OrgPropertyAccessService Service(AppDbContext db) =>
        _kit.PropertyAccess(db);

    private async Task<T> RunAsync<T>(Func<OrgPropertyAccessService, Task<T>> action)
    {
        await using var db = _kit.NewDb();
        return await action(Service(db));
    }

    private Task RunAsync(Func<OrgPropertyAccessService, Task> action) => RunAsync(async service =>
    {
        await action(service);
        return 0;
    });

    private async Task<Guid> SeedOrgAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        return org.Id;
    }

    private async Task<Guid> AddMemberAsync(Guid orgId, string userId, OrgRole role, OrgMemberStatus status = OrgMemberStatus.Active)
    {
        await _kit.SeedMemberAsync(orgId, userId, role, status: status);
        return (await _kit.ReadMemberAsync(userId))!.Id;
    }

    private async Task<Guid> AddPropertyAsync(Guid orgId, string name, bool isActive = true)
    {
        await using var db = _kit.NewDb();
        var property = HostScopeScenario.NewProperty(orgId, OwnerId, name);
        property.IsActive = isActive;
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private async Task<List<Guid>> GrantsOfAsync(string userId)
    {
        await using var db = _kit.NewDb();
        return await db.PropertyMemberAccesses.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == userId)
            .Select(a => a.PropertyId)
            .OrderBy(id => id)
            .ToListAsync();
    }

    private static List<Guid> Sorted(params Guid[] ids) => ids.OrderBy(id => id).ToList();

    private Task<OrgMemberPropertyAccessView> SetAsync(
        Guid orgId, Guid memberId, PropertyScope scope, string actor = OwnerId, params Guid[] propertyIds) =>
        RunAsync(s => s.SetAsync(orgId, memberId, actor, scope, propertyIds));

    // --- Reading the access of a member ---------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ACollaboratorWithEveryProperty_ReachesAllOfThem_AndCanBeLimited()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        var casa = await AddPropertyAsync(orgId, "Casa Bianca");

        var view = await RunAsync(s => s.GetAsync(orgId, memberId));

        Assert.Equal((OrgRole.Collaborator, PropertyScope.All, true), (view.Role, view.PropertyScope, view.ScopeSupported));
        Assert.Equal(["Casa Bianca", "Trullo"], view.Properties.Select(p => p.Name));
        Assert.All(view.Properties, p => Assert.True(p.Granted));
        Assert.Equal(Sorted(casa, trullo), view.Properties.Select(p => p.PropertyId).OrderBy(id => id));
    }

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task GetAsync_NotACollaborator_ReachesEverything_AndTheScopeCannotBeLimited(OrgRole role)
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|pia", role);
        await AddPropertyAsync(orgId, "Trullo");
        await AddPropertyAsync(orgId, "Casa Bianca");
        // A stale Selected on a role that cannot be limited (written by hand) never narrows the view.
        await using (var db = _kit.NewDb())
        {
            (await db.OrgMembers.IgnoreQueryFilters().SingleAsync(m => m.Id == memberId)).PropertyScope = PropertyScope.Selected;
            await db.SaveChangesAsync();
        }

        var view = await RunAsync(s => s.GetAsync(orgId, memberId));

        Assert.False(view.ScopeSupported);
        Assert.Equal(PropertyScope.All, view.PropertyScope);
        Assert.All(view.Properties, p => Assert.True(p.Granted));
        Assert.Equal(2, view.Properties.Count);
    }

    [Fact]
    public async Task GetAsync_ListsTheActivePropertiesOfThisOrgOnly()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var mine = await AddPropertyAsync(orgId, "Trullo");
        await AddPropertyAsync(orgId, "Chiusa", isActive: false);
        await AddPropertyAsync(other.Id, "Altrui");

        var view = await RunAsync(s => s.GetAsync(orgId, memberId));

        Assert.Equal([mine], view.Properties.Select(p => p.PropertyId));
    }

    [Fact]
    public async Task GetAsync_PeopleWithAccess_AreTheOwnerTheAdminsTheManagersAndTheCollaboratorsGivenTheProperty()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        await AddMemberAsync(orgId, "auth0|manager", OrgRole.PropertyManager);
        await AddMemberAsync(orgId, "auth0|fired", OrgRole.Admin, OrgMemberStatus.Deactivated);
        var everything = await AddMemberAsync(orgId, "auth0|everything", OrgRole.Collaborator);
        var onlyFirst = await AddMemberAsync(orgId, "auth0|first", OrgRole.Collaborator);
        var onlySecond = await AddMemberAsync(orgId, "auth0|second", OrgRole.Collaborator);
        var first = await AddPropertyAsync(orgId, "Primo");
        var second = await AddPropertyAsync(orgId, "Secondo");
        await SetAsync(orgId, onlyFirst, PropertyScope.Selected, OwnerId, first);
        await SetAsync(orgId, onlySecond, PropertyScope.Selected, OwnerId, second);
        await SetAsync(orgId, onlySecond, PropertyScope.Selected, OwnerId, second, first);

        var view = await RunAsync(s => s.GetAsync(orgId, everything));

        // The owner, the admin, the manager and the collaborator with every property reach both (four people); the deactivated
        // admin nobody. The first property is also reached by the two limited collaborators, the second by the one given it.
        Assert.Equal(6, view.Properties.Single(p => p.PropertyId == first).PeopleWithAccess);
        Assert.Equal(5, view.Properties.Single(p => p.PropertyId == second).PeopleWithAccess);
    }

    [Fact]
    public async Task GetAsync_ALimitedCollaborator_IsGrantedOnlyItsProperties()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        var casa = await AddPropertyAsync(orgId, "Casa Bianca");
        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo);

        var view = await RunAsync(s => s.GetAsync(orgId, memberId));

        Assert.Equal(PropertyScope.Selected, view.PropertyScope);
        Assert.True(view.Properties.Single(p => p.PropertyId == trullo).Granted);
        Assert.False(view.Properties.Single(p => p.PropertyId == casa).Granted);
        // The owner reaches both, the collaborator one.
        Assert.Equal(2, view.Properties.Single(p => p.PropertyId == trullo).PeopleWithAccess);
        Assert.Equal(1, view.Properties.Single(p => p.PropertyId == casa).PeopleWithAccess);
    }

    [Fact]
    public async Task GetAsync_AMemberOfAnotherOrg_IsNotFound()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var stranger = await AddMemberAsync(other.Id, "auth0|stranger", OrgRole.Collaborator);

        var error = await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.GetAsync(orgId, stranger)));

        Assert.Equal(OrgInvitationErrors.MemberNotFound, error.Code);
    }

    // --- Setting the access ---------------------------------------------------------------------------

    [Fact]
    public async Task SetAsync_Selected_GivesExactlyTheProperties_AndInvalidatesTheCacheOfTheMember()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        await AddPropertyAsync(orgId, "Casa Bianca");
        _kit.Cache.Invocations.Clear();

        var view = await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo);

        Assert.Equal(PropertyScope.Selected, view.PropertyScope);
        Assert.Equal([trullo], await GrantsOfAsync("auth0|anna"));
        Assert.Equal(PropertyScope.Selected, (await _kit.ReadMemberAsync("auth0|anna"))!.PropertyScope);
        _kit.Cache.Verify(c => c.Invalidate("auth0|anna"), Times.Once);

        await using var db = _kit.NewDb();
        var grant = await db.PropertyMemberAccesses.IgnoreQueryFilters().SingleAsync();
        Assert.Equal((orgId, OwnerId, _kit.Now), (grant.OrgId, grant.CreatedByUserId, grant.CreatedAt));
    }

    [Fact]
    public async Task SetAsync_ASecondTime_WritesTheDifferenceAndKeepsTheRowsThatStay()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var first = await AddPropertyAsync(orgId, "Primo");
        var second = await AddPropertyAsync(orgId, "Secondo");
        var third = await AddPropertyAsync(orgId, "Terzo");
        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, first, second);
        Guid keptRow;
        await using (var db = _kit.NewDb())
            keptRow = (await db.PropertyMemberAccesses.IgnoreQueryFilters().SingleAsync(a => a.PropertyId == second)).Id;

        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, second, third, third);

        Assert.Equal(Sorted(second, third), await GrantsOfAsync("auth0|anna"));
        await using var check = _kit.NewDb();
        Assert.Equal(keptRow, (await check.PropertyMemberAccesses.IgnoreQueryFilters().SingleAsync(a => a.PropertyId == second)).Id);
    }

    [Fact]
    public async Task SetAsync_All_RemovesTheGrants_AndTheMemberReachesTheWholeOrgAgain()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo);

        // The ids sent with All are ignored (even unknown ones): All means every property.
        var view = await SetAsync(orgId, memberId, PropertyScope.All, OwnerId, Guid.NewGuid());

        Assert.Equal(PropertyScope.All, view.PropertyScope);
        Assert.Empty(await GrantsOfAsync("auth0|anna"));
        Assert.Equal(PropertyScope.All, (await _kit.ReadMemberAsync("auth0|anna"))!.PropertyScope);
    }

    [Fact]
    public async Task SetAsync_SelectedWithNothing_IsAllowed_TheCollaboratorSeesNothing()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo);

        var view = await SetAsync(orgId, memberId, PropertyScope.Selected);

        Assert.Equal(PropertyScope.Selected, view.PropertyScope);
        Assert.All(view.Properties, p => Assert.False(p.Granted));
        Assert.Empty(await GrantsOfAsync("auth0|anna"));
    }

    [Fact]
    public async Task SetAsync_APropertyOfAnotherOrgOrUnknown_IsRefused_AndNothingIsWritten()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var mine = await AddPropertyAsync(orgId, "Trullo");
        var foreign = await AddPropertyAsync(other.Id, "Altrui");
        _kit.Cache.Invocations.Clear();

        foreach (var bad in new[] { foreign, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<DomainRuleException>(
                () => SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, mine, bad));
            Assert.Equal(OrgMembershipErrors.PropertyUnknown, error.Code);
        }

        Assert.Empty(await GrantsOfAsync("auth0|anna"));
        Assert.Equal(PropertyScope.All, (await _kit.ReadMemberAsync("auth0|anna"))!.PropertyScope);
        _kit.Cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task SetAsync_SelectedForARoleThatIsNotTheCollaborators_IsRefused(OrgRole role)
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|pia", role);
        var trullo = await AddPropertyAsync(orgId, "Trullo");

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo));

        Assert.Equal(OrgMembershipErrors.ScopeNotSupported, error.Code);
        Assert.Equal(PropertyScope.All, (await _kit.ReadMemberAsync("auth0|pia"))!.PropertyScope);
        Assert.Empty(await GrantsOfAsync("auth0|pia"));
    }

    [Fact]
    public async Task SetAsync_AllForTheOwner_ChangesNothing_SelectedIsRefused()
    {
        var orgId = await SeedOrgAsync();
        var ownerRow = (await _kit.ReadMemberAsync(OwnerId))!.Id;
        var trullo = await AddPropertyAsync(orgId, "Trullo");

        var view = await SetAsync(orgId, ownerRow, PropertyScope.All);
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => SetAsync(orgId, ownerRow, PropertyScope.Selected, OwnerId, trullo));

        Assert.False(view.ScopeSupported);
        Assert.Equal(OrgMembershipErrors.ScopeNotSupported, error.Code);
    }

    // --- Who may set it ------------------------------------------------------------------------------

    [Fact]
    public async Task SetAsync_AnAdminLimitsACollaborator_ButNotAnotherAdmin()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin);
        var otherAdmin = await AddMemberAsync(orgId, "auth0|admin2", OrgRole.Admin);
        var collaborator = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");

        var done = await SetAsync(orgId, collaborator, PropertyScope.Selected, "auth0|admin", trullo);
        var error = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => SetAsync(orgId, otherAdmin, PropertyScope.All, "auth0|admin"));

        Assert.Equal(PropertyScope.Selected, done.PropertyScope);
        Assert.Equal(OrgInvitationErrors.OwnerRequired, error.Code);
        // The owner may touch an administrator (here with nothing to change).
        Assert.False((await SetAsync(orgId, otherAdmin, PropertyScope.All)).ScopeSupported);
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public async Task SetAsync_ByAPersonWhoDoesNotManageThePeople_IsRefused(OrgRole actorRole)
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|actor", actorRole);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => SetAsync(orgId, memberId, PropertyScope.Selected, "auth0|actor", trullo));

        Assert.Empty(await GrantsOfAsync("auth0|anna"));
    }

    [Fact]
    public async Task SetAsync_ADeactivatedManager_IsRefused()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|admin", OrgRole.Admin, OrgMemberStatus.Deactivated);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SetAsync(orgId, memberId, PropertyScope.All, "auth0|admin"));
    }

    [Fact]
    public async Task SetAsync_AMemberOfAnotherOrg_IsNotFound_AndAnActorOfAnotherOrgIsRefused()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var stranger = await AddMemberAsync(other.Id, "auth0|stranger", OrgRole.Collaborator);
        var mine = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        var notFound = await Assert.ThrowsAsync<NotFoundException>(() => SetAsync(orgId, stranger, PropertyScope.All));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SetAsync(orgId, mine, PropertyScope.All, "auth0|other-owner"));

        Assert.Equal(OrgInvitationErrors.MemberNotFound, notFound.Code);
    }

    [Fact]
    public async Task SetAsync_AnUndefinedScope_IsRejected()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SetAsync(orgId, memberId, (PropertyScope)99));
    }

    // --- The member in charge of a property ------------------------------------------------------------

    private async Task<string?> ResponsibleOfAsync(Guid propertyId)
    {
        await using var db = _kit.NewDb();
        return (await db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId)).ResponsibleUserId;
    }

    [Fact]
    public async Task SetResponsibleAsync_AnActiveMemberWhoReachesTheProperty_CanBeInCharge()
    {
        var orgId = await SeedOrgAsync();
        await AddMemberAsync(orgId, "auth0|manager", OrgRole.PropertyManager);
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo);

        await RunAsync(s => s.SetResponsibleAsync(orgId, trullo, "auth0|anna"));
        Assert.Equal("auth0|anna", await ResponsibleOfAsync(trullo));

        await RunAsync(s => s.SetResponsibleAsync(orgId, trullo, "auth0|manager"));
        Assert.Equal("auth0|manager", await ResponsibleOfAsync(trullo));

        // Nobody: the creator is told again.
        await RunAsync(s => s.SetResponsibleAsync(orgId, trullo, null));
        Assert.Null(await ResponsibleOfAsync(trullo));
    }

    [Fact]
    public async Task SetResponsibleAsync_ALimitedCollaboratorWhoWasNotGivenTheProperty_IsRefused()
    {
        var orgId = await SeedOrgAsync();
        var memberId = await AddMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        var casa = await AddPropertyAsync(orgId, "Casa Bianca");
        await SetAsync(orgId, memberId, PropertyScope.Selected, OwnerId, trullo);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => RunAsync(s => s.SetResponsibleAsync(orgId, casa, "auth0|anna")));

        Assert.Equal(PropertyResponsibleErrors.Invalid, error.Code);
        Assert.Null(await ResponsibleOfAsync(casa));
    }

    [Fact]
    public async Task SetResponsibleAsync_ADeactivatedMemberAnInactiveAccountOrAStranger_IsRefused()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await AddMemberAsync(orgId, "auth0|fired", OrgRole.PropertyManager, OrgMemberStatus.Deactivated);
        await AddMemberAsync(orgId, "auth0|locked", OrgRole.PropertyManager);
        await AddMemberAsync(other.Id, "auth0|stranger", OrgRole.PropertyManager);
        var trullo = await AddPropertyAsync(orgId, "Trullo");
        await using (var db = _kit.NewDb())
        {
            (await db.Users.SingleAsync(u => u.Id == "auth0|locked")).IsActive = false;
            await db.SaveChangesAsync();
        }

        foreach (var person in new[] { "auth0|fired", "auth0|locked", "auth0|stranger", "auth0|nobody" })
        {
            var error = await Assert.ThrowsAsync<DomainRuleException>(() => RunAsync(s => s.SetResponsibleAsync(orgId, trullo, person)));
            Assert.Equal(PropertyResponsibleErrors.Invalid, error.Code);
        }

        Assert.Null(await ResponsibleOfAsync(trullo));
    }

    [Fact]
    public async Task SetResponsibleAsync_APropertyOfAnotherOrg_IsNotFound()
    {
        var orgId = await SeedOrgAsync();
        var (other, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        var foreign = await AddPropertyAsync(other.Id, "Altrui");

        var error = await Assert.ThrowsAsync<NotFoundException>(() => RunAsync(s => s.SetResponsibleAsync(orgId, foreign, OwnerId)));

        Assert.Equal("property_not_found", error.Code);
        Assert.Null(await ResponsibleOfAsync(foreign));
    }
}
