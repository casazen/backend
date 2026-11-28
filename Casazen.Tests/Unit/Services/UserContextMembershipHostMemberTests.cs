using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class UserContextMembershipHostMemberTests
{
    private const string UserId = "auth0|host-member";
    private const string OtherUserId = "auth0|someone-else";

    private const int ShortRentOwnerRoleId = 1;
    private const int LongRentOwnerRoleId = 2;
    private const int PlatformAdminRoleId = 3;
    private const int ShortRentCollaboratorRoleId = 101;
    private const int LongRentCollaboratorRoleId = 102;

    [Fact]
    public async Task IsHostMemberAsync_NoMembership_IsFalse()
    {
        await using var db = await CreateSeededDbAsync();

        Assert.False(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Theory]
    [InlineData("short-rent", ShortRentOwnerRoleId)]
    [InlineData("long-rent", LongRentOwnerRoleId)]
    [InlineData("admin", PlatformAdminRoleId)]
    public async Task IsHostMemberAsync_OwnerOrPlatformAdminMembership_IsFalse(string contextKey, int roleId)
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.Add(new UserContextMembership { UserId = UserId, ContextKey = contextKey, RoleId = roleId });
        await db.SaveChangesAsync();

        Assert.False(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_BothOwnerMemberships_IsFalse()
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.AddRange(
            new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = ShortRentOwnerRoleId },
            new UserContextMembership { UserId = UserId, ContextKey = "long-rent", RoleId = LongRentOwnerRoleId });
        await db.SaveChangesAsync();

        Assert.False(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Theory]
    [InlineData("short-rent", ShortRentCollaboratorRoleId)]
    [InlineData("long-rent", LongRentCollaboratorRoleId)]
    public async Task IsHostMemberAsync_CollaboratorRoleOfAHostContext_IsTrue(string contextKey, int roleId)
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.Add(new UserContextMembership { UserId = UserId, ContextKey = contextKey, RoleId = roleId });
        await db.SaveChangesAsync();

        Assert.True(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_OwnerOfOneContextAndCollaboratorOfTheOther_IsTrue()
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.AddRange(
            new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = ShortRentOwnerRoleId },
            new UserContextMembership { UserId = UserId, ContextKey = "long-rent", RoleId = LongRentCollaboratorRoleId });
        await db.SaveChangesAsync();

        Assert.True(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_PlatformAdminAlsoCollaboratorOfAHostContext_IsTrue()
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.AddRange(
            new UserContextMembership { UserId = UserId, ContextKey = "admin", RoleId = PlatformAdminRoleId },
            new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = ShortRentCollaboratorRoleId });
        await db.SaveChangesAsync();

        Assert.True(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_MembershipOfAnotherUser_DoesNotCount()
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.Add(
            new UserContextMembership { UserId = OtherUserId, ContextKey = "short-rent", RoleId = ShortRentCollaboratorRoleId });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        Assert.False(await service.IsHostMemberAsync(UserId));
        Assert.True(await service.IsHostMemberAsync(OtherUserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_MembershipAddedAfterTheFirstAnswer_IsSeenAtOnce()
    {
        await using var db = await CreateSeededDbAsync();
        var service = CreateService(db);
        Assert.False(await service.IsHostMemberAsync(UserId));

        db.UserContextMemberships.Add(new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = ShortRentCollaboratorRoleId });
        await db.SaveChangesAsync();

        Assert.True(await service.IsHostMemberAsync(UserId));
    }

    [Theory]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Collaborator)]
    [InlineData(OrgRole.Accountant)]
    public async Task IsHostMemberAsync_OrgMemberThatIsNotTheOwner_IsTrueWhateverItsMemberships(OrgRole role)
    {
        await using var db = await CreateSeededDbAsync();
        db.OrgMembers.Add(new OrgMember { UserId = UserId, OrgId = Guid.NewGuid(), Role = role });
        await db.SaveChangesAsync();

        Assert.True(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_OwnerOrgMember_IsFalse()
    {
        await using var db = await CreateSeededDbAsync();
        db.OrgMembers.Add(new OrgMember { UserId = UserId, OrgId = Guid.NewGuid(), Role = OrgRole.Owner });
        db.UserContextMemberships.Add(new UserContextMembership
        {
            UserId = UserId,
            ContextKey = "account",
            RoleId = db.Roles.Single(r => r.ContextKey == "account" && r.RoleKey == "org_owner").Id,
        });
        await db.SaveChangesAsync();

        Assert.False(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_OrgMemberOfAnotherUser_DoesNotCount()
    {
        await using var db = await CreateSeededDbAsync();
        db.OrgMembers.Add(new OrgMember { UserId = OtherUserId, OrgId = Guid.NewGuid(), Role = OrgRole.Collaborator });
        await db.SaveChangesAsync();

        var service = CreateService(db);

        Assert.False(await service.IsHostMemberAsync(UserId));
        Assert.True(await service.IsHostMemberAsync(OtherUserId));
    }

    [Theory]
    [InlineData("org_owner", false)]
    [InlineData("org_admin", false)]
    [InlineData("org_accountant", true)]
    public async Task IsHostMemberAsync_AccountMembershipAlone_OnlyTheAccountantIsAMember(string roleKey, bool expected)
    {
        await using var db = await CreateSeededDbAsync();
        db.UserContextMemberships.Add(new UserContextMembership
        {
            UserId = UserId,
            ContextKey = "account",
            RoleId = db.Roles.Single(r => r.ContextKey == "account" && r.RoleKey == roleKey).Id,
        });
        await db.SaveChangesAsync();

        Assert.Equal(expected, await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_OrgIdOfAHostOrgThatAlreadyHasAnOwner_IsTrue()
    {
        await using var db = await CreateSeededDbAsync();
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity { Id = orgId, Name = "Host", DisplayName = "Host", Slug = "host", OrgType = OrgType.Host });
        db.Users.Single(u => u.Id == UserId).OrgId = orgId;
        db.OrgMembers.Add(new OrgMember { UserId = OtherUserId, OrgId = orgId, Role = OrgRole.Owner });
        await db.SaveChangesAsync();

        Assert.True(await CreateService(db).IsHostMemberAsync(UserId));
    }

    [Fact]
    public async Task IsHostMemberAsync_OrgIdOfAHostOrgWithNoOwnerYet_IsFalse()
    {
        await using var db = await CreateSeededDbAsync();
        var orgId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity { Id = orgId, Name = "New", DisplayName = "New", Slug = "new", OrgType = OrgType.Host });
        db.Users.Single(u => u.Id == UserId).OrgId = orgId;
        await db.SaveChangesAsync();

        Assert.False(await CreateService(db).IsHostMemberAsync(UserId));
    }

    private static UserContextMembershipService CreateService(AppDbContext db) =>
        new(db, Mock.Of<IUserAuthorizationCache>(), NullLogger<UserContextMembershipService>.Instance);

    private static async Task<AppDbContext> CreateSeededDbAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        await db.Database.EnsureCreatedAsync();
        db.Roles.AddRange(
            new Role { Id = ShortRentCollaboratorRoleId, ContextKey = "short-rent", RoleKey = "bk09_collaborator" },
            new Role { Id = LongRentCollaboratorRoleId, ContextKey = "long-rent", RoleKey = "staff" });
        db.Users.AddRange(
            new User { Id = UserId, Email = "host-member@test.com", FirstName = "Host", LastName = "Member", IsActive = true },
            new User { Id = OtherUserId, Email = "other@test.com", FirstName = "Other", LastName = "User", IsActive = true });
        await db.SaveChangesAsync();
        return db;
    }
}
