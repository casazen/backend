using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-00 (S2): <see cref="UserContextMembershipService.IsHostMemberAsync"/> tells a member of an org (a DB membership of a
/// host context with a role other than the owner's) from its owner, a platform admin and a user with no membership.
/// The seed has the owner roles (1 <c>property_owner</c> of short-rent, 2 <c>long_term_landlord</c> of long-rent,
/// 3 <c>platform_admin</c> of admin).
/// </summary>
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
        // Any member role makes a member: the onboarding must not be able to rewrite it.
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
        // Read from the database on every call, never memoized: it guards a self-service action.
        await using var db = await CreateSeededDbAsync();
        var service = CreateService(db);
        Assert.False(await service.IsHostMemberAsync(UserId));

        db.UserContextMemberships.Add(new UserContextMembership { UserId = UserId, ContextKey = "short-rent", RoleId = ShortRentCollaboratorRoleId });
        await db.SaveChangesAsync();

        Assert.True(await service.IsHostMemberAsync(UserId));
    }

    private static UserContextMembershipService CreateService(AppDbContext db) =>
        new(db, Mock.Of<IUserAuthorizationCache>(), NullLogger<UserContextMembershipService>.Instance);

    private static async Task<AppDbContext> CreateSeededDbAsync()
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        // Applies the HasData seed: contexts short-rent/long-rent/admin and the owner roles 1..3.
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
