using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Xunit;
using static Casazen.Tests.Unit.Services.OrgTeamTestData;

namespace Casazen.Tests.Unit.Services;

public class OrgHolderServiceTests
{
    private const string Creator = "auth0|creator";

    private static async Task<(AppDbContext Db, Guid OrgId)> NewOrgAsync()
    {
        var db = NewDb();
        var org = AddOrg(db);
        AddUser(db, Creator, org.Id);
        await db.SaveChangesAsync();
        return (db, org.Id);
    }

    private static async Task AddMemberOfAsync(AppDbContext db, Guid orgId, string userId, OrgRole role, OrgMemberStatus status = OrgMemberStatus.Active)
    {
        AddUser(db, userId, orgId);
        AddMember(db, userId, orgId, role, status);
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(OrgRole.Owner, true)]
    [InlineData(OrgRole.Admin, true)]
    [InlineData(OrgRole.PropertyManager, false)]
    [InlineData(OrgRole.Collaborator, false)]
    [InlineData(OrgRole.Accountant, false)]
    public async Task IsHolderAsync_OnlyTheOwnerAndTheAdminsOfTheOrgAreTheHolder_WhoeverCreatedTheProperty(OrgRole role, bool expected)
    {
        var (db, orgId) = await NewOrgAsync();
        await using var _ = db;
        await AddMemberOfAsync(db, orgId, "auth0|member", role);

        Assert.Equal(expected, await new OrgHolderService(db).IsHolderAsync("auth0|member", orgId, Creator));
        Assert.Equal(expected, await new OrgHolderService(db).IsHolderAsync("auth0|member", orgId, "auth0|member"));
    }

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    public async Task IsHolderAsync_ADeactivatedOwnerOrAdmin_IsNotTheHolder(OrgRole role)
    {
        var (db, orgId) = await NewOrgAsync();
        await using var _ = db;
        await AddMemberOfAsync(db, orgId, "auth0|member", role, OrgMemberStatus.Deactivated);

        Assert.False(await new OrgHolderService(db).IsHolderAsync("auth0|member", orgId, Creator));
    }

    [Fact]
    public async Task IsHolderAsync_TheOwnerOfAnotherOrg_IsNotTheHolderOfThisOne()
    {
        var (db, orgId) = await NewOrgAsync();
        await using var _ = db;
        var other = AddOrg(db);
        await db.SaveChangesAsync();
        await AddMemberOfAsync(db, other.Id, "auth0|stranger", OrgRole.Owner);

        Assert.False(await new OrgHolderService(db).IsHolderAsync("auth0|stranger", orgId, Creator));
        Assert.True(await new OrgHolderService(db).IsHolderAsync("auth0|stranger", other.Id, Creator));
    }

    [Fact]
    public async Task IsHolderAsync_AnAccountInNoTeam_IsTheHolderOfThePropertiesItCreated_AsBeforeTheTeam()
    {
        var (db, orgId) = await NewOrgAsync();
        await using var _ = db;

        Assert.True(await new OrgHolderService(db).IsHolderAsync(Creator, orgId, Creator));
        Assert.False(await new OrgHolderService(db).IsHolderAsync(Creator, orgId, "auth0|somebody-else"));
        Assert.False(await new OrgHolderService(db).IsHolderAsync(Creator, orgId, "AUTH0|CREATOR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task IsHolderAsync_NoUser_IsNeverTheHolder(string userId)
    {
        var (db, orgId) = await NewOrgAsync();
        await using var _ = db;

        Assert.False(await new OrgHolderService(db).IsHolderAsync(userId, orgId, userId));
    }
}
