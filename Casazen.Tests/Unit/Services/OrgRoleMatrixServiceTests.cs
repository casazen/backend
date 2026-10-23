using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02c: <c>OrgRoleMatrixService</c> reads the matrix of the roles from the rows of the database (<c>Roles</c>,
/// <c>RolePermissions</c>), not from a copy: change a permission of a seeded role and the level of the capability follows. The pure
/// function that builds the matrix is proved in <c>OrgCapabilityCatalogTests</c>; here it is the reading, the caller and the org.
/// </summary>
public class OrgRoleMatrixServiceTests
{
    private const string OwnerId = "auth0|owner";

    private readonly OrgInvitationTestKit _kit = new();

    [Fact]
    public async Task GetAsync_ReadsTheFiveRolesFromTheRowsTheModelSeeds_OwnerFirst()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();

        await using var db = _kit.NewDb();
        var roles = await _kit.RoleMatrix(db).GetAsync(org.Id, OwnerId);

        Assert.Equal([OrgRole.Owner, OrgRole.Admin, OrgRole.PropertyManager, OrgRole.Collaborator, OrgRole.Accountant], roles.Select(r => r.Role));
        var manager = roles.Single(r => r.Role == OrgRole.PropertyManager);
        Assert.Equal(AccountContext.RoleKeys.Manager, manager.PermissionsByContext[AccountContext.Key].RoleKey);
        Assert.Equal([AccountContext.Permissions.SuppliersManage], manager.PermissionsByContext[AccountContext.Key].Permissions);
        var collaborator = roles.Single(r => r.Role == OrgRole.Collaborator);
        Assert.DoesNotContain(AccountContext.Key, collaborator.PermissionsByContext.Keys);
        Assert.Equal("staff", collaborator.PermissionsByContext[OrgRoleCatalog.ShortRent].RoleKey);
        Assert.Equal(OrgCapabilityCatalog.All.Select(c => c.Id), collaborator.Capabilities.Select(c => c.Id));
    }

    [Fact]
    public async Task GetAsync_TheLevelFollowsThePermissionsOfTheDatabase_NotACopyOfThem()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var before = _kit.NewDb())
        {
            var guests = (await _kit.RoleMatrix(before).GetAsync(org.Id, OwnerId))
                .Single(r => r.Role == OrgRole.Collaborator).Capabilities.Single(c => c.Id == "guests");
            Assert.Equal(OrgCapabilityLevel.Yes, guests.Level);
        }

        // The collaborator loses the permission that writes a guest: it still reads them.
        await using (var admin = _kit.NewDb())
        {
            var staff = await admin.Roles.Include(r => r.Permissions)
                .SingleAsync(r => r.ContextKey == OrgRoleCatalog.ShortRent && r.RoleKey == "staff");
            admin.RolePermissions.RemoveRange(staff.Permissions.Where(p => p.PermissionKey == "guest.write"));
            await admin.SaveChangesAsync();
        }

        await using var after = _kit.NewDb();
        var level = (await _kit.RoleMatrix(after).GetAsync(org.Id, OwnerId))
            .Single(r => r.Role == OrgRole.Collaborator).Capabilities.Single(c => c.Id == "guests").Level;
        Assert.Equal(OrgCapabilityLevel.Read, level);
    }

    [Fact]
    public async Task GetAsync_TheAssignableFlag_IsRelativeToTheCaller()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await _kit.SeedMemberAsync(org.Id, "auth0|ada", OrgRole.Admin);
        await _kit.SeedMemberAsync(org.Id, "auth0|carla", OrgRole.Collaborator);

        await using var db = _kit.NewDb();
        var byOwner = await _kit.RoleMatrix(db).GetAsync(org.Id, OwnerId);
        var byAdmin = await _kit.RoleMatrix(db).GetAsync(org.Id, "auth0|ada");
        var byCollaborator = await _kit.RoleMatrix(db).GetAsync(org.Id, "auth0|carla");

        Assert.Equal([OrgRole.Admin, OrgRole.PropertyManager, OrgRole.Collaborator, OrgRole.Accountant], byOwner.Where(r => r.Assignable).Select(r => r.Role));
        Assert.Equal([OrgRole.PropertyManager, OrgRole.Collaborator, OrgRole.Accountant], byAdmin.Where(r => r.Assignable).Select(r => r.Role));
        Assert.DoesNotContain(byCollaborator, r => r.Assignable);
        // The rest of the matrix is the same for everybody.
        Assert.Equal(
            byOwner.Select(r => (r.Role, r.ScopeSupported, string.Join(',', r.Capabilities.Select(c => c.Level)))),
            byCollaborator.Select(r => (r.Role, r.ScopeSupported, string.Join(',', r.Capabilities.Select(c => c.Level)))));
    }

    [Fact]
    public async Task GetAsync_APersonWhoIsNotAnActiveMemberOfTheOrg_IsRefused()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        var (otherOrg, _) = await _kit.SeedOwnerOrgAsync("auth0|other-owner");
        await _kit.SeedMemberAsync(org.Id, "auth0|dora", OrgRole.Collaborator, status: OrgMemberStatus.Deactivated);

        await using var db = _kit.NewDb();
        var service = _kit.RoleMatrix(db);

        // A member of another org, a deactivated member and a stranger: nothing of this org's matrix.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(org.Id, "auth0|other-owner"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(org.Id, "auth0|dora"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(org.Id, "auth0|nobody"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(otherOrg.Id, OwnerId));
    }
}
