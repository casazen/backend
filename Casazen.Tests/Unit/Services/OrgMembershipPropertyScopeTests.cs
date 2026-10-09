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

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-03: what the writes of the org people do with the properties a member reaches. Only the collaborator can be limited
/// ("Solo alcuni"); the grants end with the role that made them possible (another role, removal), and stay through a
/// deactivation. The real services over EF InMemory.
/// </summary>
public class OrgMembershipPropertyScopeTests
{
    private const string OwnerId = "auth0|owner";

    private readonly OrgInvitationTestKit _kit = new();

    private async Task<(Guid OrgId, Guid PropertyA, Guid PropertyB)> SeedOrgWithPropertiesAsync()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();
        var a = HostScopeScenario.NewProperty(org.Id, OwnerId, "Trullo");
        var b = HostScopeScenario.NewProperty(org.Id, OwnerId, "Casa Bianca");
        db.Properties.AddRange(a, b);
        await db.SaveChangesAsync();
        return (org.Id, a.Id, b.Id);
    }

    private async Task<OrgMember> LimitedCollaboratorAsync(Guid orgId, params Guid[] propertyIds)
    {
        var (_, member) = await _kit.SeedMemberAsync(orgId, "auth0|anna", OrgRole.Collaborator);
        await using var db = _kit.NewDb();
        var service = new OrgPropertyAccessService(db, _kit.Cache.Object, NullLogger<OrgPropertyAccessService>.Instance, _kit.Clock);
        await service.SetAsync(orgId, member.Id, OwnerId, PropertyScope.Selected, propertyIds);
        return member;
    }

    private async Task<List<Guid>> GrantsAsync(string userId)
    {
        await using var db = _kit.NewDb();
        return await db.PropertyMemberAccesses.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.UserId == userId).Select(a => a.PropertyId).OrderBy(id => id).ToListAsync();
    }

    private async Task<PropertyScope> ScopeOfAsync(string userId) => (await _kit.ReadMemberAsync(userId))!.PropertyScope;

    // --- Adding a member ------------------------------------------------------------------------------

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task AddMemberAsync_SoloAlcuniForARoleThatIsNotTheCollaborators_IsRefused(OrgRole role)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();
        OrgTeamTestData.AddUser(db, "auth0|pia", orgId: null);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => _kit.Membership(db).AddMemberAsync("auth0|pia", org.Id, role, ["short-rent"], OwnerId, PropertyScope.Selected));

        Assert.Equal(OrgMembershipErrors.ScopeNotSupported, error.Code);
        Assert.Null(await _kit.ReadMemberAsync("auth0|pia"));
    }

    [Fact]
    public async Task AddMemberAsync_SoloAlcuniForACollaborator_IsStored_AndSeesNothingUntilItIsGivenProperties()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();
        OrgTeamTestData.AddUser(db, "auth0|anna", orgId: null);
        await db.SaveChangesAsync();

        await _kit.Membership(db).AddMemberAsync("auth0|anna", org.Id, OrgRole.Collaborator, ["short-rent"], OwnerId, PropertyScope.Selected);

        Assert.Equal(PropertyScope.Selected, await ScopeOfAsync("auth0|anna"));
        Assert.Empty(await GrantsAsync("auth0|anna"));
    }

    // --- Changing the role -----------------------------------------------------------------------------

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    [InlineData(OrgRole.Admin)]
    public async Task ChangeRoleAsync_ALimitedCollaboratorBecomesAnotherRole_ReachesEverything_AndTheGrantsAreGone(OrgRole role)
    {
        var (orgId, a, _) = await SeedOrgWithPropertiesAsync();
        await LimitedCollaboratorAsync(orgId, a);
        _kit.Cache.Invocations.Clear();

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).ChangeRoleAsync("auth0|anna", role);

        Assert.Equal(PropertyScope.All, await ScopeOfAsync("auth0|anna"));
        Assert.Empty(await GrantsAsync("auth0|anna"));
        _kit.Cache.Verify(c => c.Invalidate("auth0|anna"), Times.AtLeastOnce);

        // Made a collaborator again, the person reaches every property: the old grants do not come back to life.
        await using (var db = _kit.NewDb())
            await _kit.Membership(db).ChangeRoleAsync("auth0|anna", OrgRole.Collaborator);
        Assert.Equal(PropertyScope.All, await ScopeOfAsync("auth0|anna"));
        Assert.Empty(await GrantsAsync("auth0|anna"));
    }

    // --- Removing, deactivating ------------------------------------------------------------------------

    [Fact]
    public async Task RemoveAsync_ALimitedCollaborator_LeavesNoGrantBehind()
    {
        var (orgId, a, b) = await SeedOrgWithPropertiesAsync();
        await LimitedCollaboratorAsync(orgId, a, b);

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).RemoveAsync("auth0|anna");

        Assert.Empty(await GrantsAsync("auth0|anna"));
        Assert.Null(await _kit.ReadMemberAsync("auth0|anna"));
    }

    [Fact]
    public async Task DeactivateAndReactivateAsync_KeepTheGrants_ThePersonComesBackWithTheSameProperties()
    {
        var (orgId, a, _) = await SeedOrgWithPropertiesAsync();
        await LimitedCollaboratorAsync(orgId, a);

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).DeactivateAsync("auth0|anna");
        Assert.Equal([a], await GrantsAsync("auth0|anna"));
        Assert.Equal(PropertyScope.Selected, await ScopeOfAsync("auth0|anna"));

        await using (var db = _kit.NewDb())
            await _kit.Membership(db).ReactivateAsync("auth0|anna");
        Assert.Equal([a], await GrantsAsync("auth0|anna"));
        Assert.Equal(PropertyScope.Selected, await ScopeOfAsync("auth0|anna"));
    }

    // --- Inviting --------------------------------------------------------------------------------------

    private static CreateOrgInvitation Invitation(Guid orgId, OrgRole role, PropertyScope scope) =>
        new(orgId, OwnerId, "anna.leone@example.com", "Anna Leone", role, ["short-rent"], scope);

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task InvitationCreateAsync_SoloAlcuniForARoleThatIsNotTheCollaborators_IsRefusedBeforeAnythingIsWritten(OrgRole role)
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();

        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => _kit.Invitations(db).CreateAsync(Invitation(org.Id, role, PropertyScope.Selected)));

        Assert.Equal(OrgMembershipErrors.ScopeNotSupported, error.Code);
        Assert.Empty(_kit.Emails.Snapshot());
        await using var check = _kit.NewDb();
        Assert.Empty(await check.OrgInvitations.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task InvitationCreateAsync_SoloAlcuniForACollaborator_IsAccepted()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();

        var sent = await _kit.Invitations(db).CreateAsync(Invitation(org.Id, OrgRole.Collaborator, PropertyScope.Selected));

        Assert.Equal(PropertyScope.Selected, (await _kit.ReadInvitationAsync(sent.Invitation.Id)).PropertyScope);
    }

    [Theory]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    [InlineData(OrgRole.Admin)]
    public async Task InvitationAcceptAsync_ALegacyInvitationWithSoloAlcuniForAnotherRole_MakesAMemberWhoReachesTheWholeOrg(OrgRole role)
    {
        // An invitation sent before the rule existed: the person must not be stuck on it, and reaches everything as the role does.
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedNewUserAsync("auth0|anna", "anna.leone@example.com");
        var (invitation, token) = await _kit.SeedInvitationAsync(org.Id, "anna.leone@example.com", role);
        await using (var db = _kit.NewDb())
        {
            (await db.OrgInvitations.IgnoreQueryFilters().SingleAsync(i => i.Id == invitation.Id)).PropertyScope = PropertyScope.Selected;
            await db.SaveChangesAsync();
        }

        await using (var db = _kit.NewDb())
        {
            await _kit.Invitations(db).AcceptAsync(
                new AcceptOrgInvitation(token, "auth0|anna", "anna.leone@example.com", true, false, OrgInvitationTestKit.Consents(), "203.0.113.7"));
        }

        var member = await _kit.ReadMemberAsync("auth0|anna");
        Assert.Equal((role, PropertyScope.All), (member!.Role, member.PropertyScope));
    }
}
