using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class UserServiceTests
{
    private readonly Mock<IUserRepository> _repoMock;
    private readonly Mock<IAuth0ManagementService> _auth0Mock;
    private readonly Mock<IOrgService> _orgMock;
    private readonly Mock<IUserContextMembershipService> _membershipMock;
    private readonly Mock<IUserAuthorizationCache> _cacheMock;
    private readonly Mock<ILogger<UserService>> _loggerMock;
    private readonly UserService _service;

    public UserServiceTests()
    {
        _repoMock = new Mock<IUserRepository>();
        _loggerMock = new Mock<ILogger<UserService>>();
        _auth0Mock = new Mock<IAuth0ManagementService>();
        _orgMock = new Mock<IOrgService>();
        _membershipMock = new Mock<IUserContextMembershipService>();
        _cacheMock = new Mock<IUserAuthorizationCache>();

        _auth0Mock.Setup(a => a.AssignRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0Mock.Setup(a => a.RemoveRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0Mock.Setup(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0Mock.Setup(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);

        _service = new UserService(
            _repoMock.Object,
            _auth0Mock.Object,
            _orgMock.Object,
            _membershipMock.Object,
            _cacheMock.Object,
            _loggerMock.Object);
    }

    // ─── GetCurrentUserAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentUserAsync_UserExists_ReturnsExistingUser()
    {
        // Arrange
        var sub = "auth0|existing123";
        var existing = new User { Id = sub, Email = "test@example.com", FirstName = "Mario", LastName = "Rossi" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(existing);

        // Act
        var result = await _service.GetCurrentUserAsync(sub, "test@example.com", "Mario", "Rossi");

        // Assert
        Assert.Equal(existing, result);
        _repoMock.Verify(r => r.AddIfAbsentAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task GetCurrentUserAsync_UserExistsWithEmptyEmail_BackfillsFromJwtClaims()
    {
        var sub = "auth0|legacy456";
        var existing = new User { Id = sub, Email = "", FirstName = "", LastName = "" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(existing);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);

        var result = await _service.GetCurrentUserAsync(sub, "luca@example.com", "Luca", "Rossi");

        Assert.Equal("luca@example.com", result.Email);
        Assert.Equal("Luca", result.FirstName);
        Assert.Equal("Rossi", result.LastName);
        _repoMock.Verify(r => r.UpdateAsync(It.Is<User>(u => u.Email == "luca@example.com")), Times.Once);
    }

    [Fact]
    public async Task GetCurrentUserAsync_UserNotExists_CreatesAndReturnsNewUser()
    {
        // Arrange
        var sub = "auth0|newuser456";
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync((User?)null);
        _repoMock.Setup(r => r.AddIfAbsentAsync(It.IsAny<User>()))
                 .ReturnsAsync((User u) => (u, true));

        // Act
        var result = await _service.GetCurrentUserAsync(sub, "new@example.com", "Luigi", "Verdi");

        // Assert
        Assert.Equal(sub, result.Id);
        Assert.Equal("new@example.com", result.Email);
        Assert.Equal("Luigi", result.FirstName);
        Assert.True(result.IsActive);
        // PL-02 (A1-05): no host role before the onboarding and its consents.
        Assert.Equal(UserRole.None, result.Role);
        Assert.Null(result.OnboardingCompletedAt);
        _repoMock.Verify(r => r.AddIfAbsentAsync(It.Is<User>(u => u.Id == sub)), Times.Once);
        _cacheMock.Verify(c => c.Invalidate(sub), Times.Once);
    }

    [Fact]
    public async Task GetCurrentUserAsync_ParallelFirstRequestInsertedTheUser_ReturnsTheStoredUser()
    {
        // A1-14: the insert lost the race; the repository returns the row of the parallel request.
        var sub = "auth0|race789";
        var stored = new User { Id = sub, Email = "first@example.com", FirstName = "Anna", LastName = "Neri", OrgId = Guid.NewGuid() };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync((User?)null);
        _repoMock.Setup(r => r.AddIfAbsentAsync(It.IsAny<User>())).ReturnsAsync((stored, false));

        var result = await _service.GetCurrentUserAsync(sub, "first@example.com", "Anna", "Neri");

        Assert.Same(stored, result);
        Assert.Equal(stored.OrgId, result.OrgId);
    }

    // ─── ChangeRoleAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ChangeRoleAsync_UserExists_AddsNewRoleAndRemovesOnlyPreviousRole()
    {
        // Arrange
        var userId = "auth0|user789";
        var adminSub = "auth0|admin000";
        var user = new User { Id = userId, Email = "user@example.com", Role = UserRole.PropertyOwner };

        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ChangeRoleAsync(userId, UserRole.Admin, adminSub);

        // Assert
        Assert.True(result.Succeeded);
        Assert.Equal(UserRole.Admin, user.Role);
        _repoMock.Verify(r => r.UpdateAsync(user), Times.Once);
        _auth0Mock.Verify(a => a.AssignRoleAsync(userId, UserRole.Admin, It.IsAny<CancellationToken>()), Times.Once);
        _auth0Mock.Verify(a => a.RemoveRoleAsync(userId, UserRole.PropertyOwner, It.IsAny<CancellationToken>()), Times.Once);
        _auth0Mock.Verify(
            a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _membershipMock.Verify(m => m.RevokeAsync(
            userId,
            It.Is<IEnumerable<UserRole>>(roles => roles.SequenceEqual(new[] { UserRole.PropertyOwner })),
            It.IsAny<CancellationToken>()), Times.Once);
        _membershipMock.Verify(m => m.GrantAsync(
            userId,
            It.Is<IEnumerable<UserRole>>(roles => roles.SequenceEqual(new[] { UserRole.Admin })),
            It.IsAny<CancellationToken>()), Times.Once);
        _cacheMock.Verify(c => c.Invalidate(userId), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ChangeRoleAsync_Auth0Fails_ReturnsFailureAndLeavesDbUnchanged()
    {
        var userId = "auth0|user-fail";
        var user = new User { Id = userId, Email = "user@example.com", Role = UserRole.PropertyOwner };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _auth0Mock.Setup(a => a.AssignRoleAsync(userId, UserRole.Admin, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode));

        var result = await _service.ChangeRoleAsync(userId, UserRole.Admin, "auth0|admin");

        Assert.False(result.Succeeded);
        Assert.Equal(Auth0SyncResult.ApiErrorCode, result.ErrorCode);
        Assert.Equal(UserRole.PropertyOwner, user.Role);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
        _auth0Mock.Verify(a => a.RemoveRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
        _membershipMock.Verify(m => m.GrantAsync(It.IsAny<string>(), It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _membershipMock.Verify(m => m.RevokeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChangeRoleAsync_Auth0NotConfigured_ReturnsNotConfigured()
    {
        var userId = "auth0|user-noconf";
        var user = new User { Id = userId, Email = "user@example.com", Role = UserRole.PropertyOwner };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _auth0Mock.Setup(a => a.AssignRoleAsync(userId, UserRole.LongTermLandlord, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.NotConfigured);

        var result = await _service.ChangeRoleAsync(userId, UserRole.LongTermLandlord, "auth0|admin");

        Assert.Equal(Auth0SyncStatus.NotConfigured, result.Status);
        Assert.Equal(Auth0SyncResult.NotConfiguredCode, result.ErrorCode);
        Assert.Equal(UserRole.PropertyOwner, user.Role);
    }

    [Fact]
    public async Task ChangeRoleAsync_UserNotFound_ThrowsKeyNotFoundException()
    {
        // Arrange
        _repoMock.Setup(r => r.GetByIdAsync("nonexistent")).ReturnsAsync((User?)null);

        // Act + Assert
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.ChangeRoleAsync("nonexistent", UserRole.Admin, "admin"));
    }

    [Fact]
    public async Task ChangeRoleAsync_DeactivatedUser_ThrowsUserInactiveWithoutAuth0Calls()
    {
        var userId = "auth0|user-inactive";
        _repoMock.Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User { Id = userId, Role = UserRole.PropertyOwner, IsActive = false });

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.ChangeRoleAsync(userId, UserRole.Admin, "auth0|admin"));

        Assert.Equal(UserActivationErrors.UserInactive, ex.Code);
        _auth0Mock.Verify(a => a.AssignRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    // ─── UpdateRolesAsync / GetRolesAsync (A1-17) ────────────────────────────

    [Fact]
    public async Task UpdateRolesAsync_AddsSupplierWithoutTouchingExistingHostRoles()
    {
        // Arrange: a dual-role "Both" host (PropertyOwner + LongTermLandlord) that the admin also makes a supplier.
        var userId = "auth0|dual-role";
        var user = new User { Id = userId, Role = UserRole.PropertyOwner };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _auth0Mock.Setup(a => a.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner, UserRole.LongTermLandlord]));

        // Act
        var result = await _service.UpdateRolesAsync(
            userId, [UserRole.PropertyOwner, UserRole.LongTermLandlord, UserRole.Supplier], "auth0|admin");

        // Assert
        Assert.True(result.RoleSync.Succeeded);
        Assert.Equal(UserRole.PropertyOwner, user.Role); // highest-priority role still held stays primary
        Assert.Equal([UserRole.Supplier], result.RolesGranted);
        Assert.Empty(result.RolesRevoked);
        _auth0Mock.Verify(a => a.AssignRolesAsync(
            userId, It.Is<IReadOnlyCollection<UserRole>>(r => r.SequenceEqual(new[] { UserRole.Supplier })), It.IsAny<CancellationToken>()), Times.Once);
        _auth0Mock.Verify(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _membershipMock.Verify(m => m.GrantAsync(
            userId, It.Is<IEnumerable<UserRole>>(r => r.SequenceEqual(new[] { UserRole.Supplier })), It.IsAny<CancellationToken>()), Times.Once);
        _membershipMock.Verify(m => m.RevokeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRolesAsync_RemovesOneOfSeveralRoles_KeepsTheOthers()
    {
        // Arrange: admin unchecks Supplier only; Admin (primary) and PropertyOwner stay.
        var userId = "auth0|multi-admin";
        var user = new User { Id = userId, Role = UserRole.Admin };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _repoMock.Setup(r => r.HasOtherActiveAdminAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _auth0Mock.Setup(a => a.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.Admin, UserRole.PropertyOwner, UserRole.Supplier]));

        // Act
        var result = await _service.UpdateRolesAsync(userId, [UserRole.Admin, UserRole.PropertyOwner], "auth0|admin");

        // Assert
        Assert.True(result.RoleSync.Succeeded);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Equal([UserRole.Supplier], result.RolesRevoked);
        Assert.Empty(result.RolesGranted);
        _auth0Mock.Verify(a => a.RemoveRolesAsync(
            userId, It.Is<IReadOnlyCollection<UserRole>>(r => r.SequenceEqual(new[] { UserRole.Supplier })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateRolesAsync_RemovingAdminFromLastActiveAdmin_ThrowsWithoutAuth0Calls()
    {
        var userId = "auth0|only-admin";
        var user = new User { Id = userId, Role = UserRole.Admin };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _repoMock.Setup(r => r.HasOtherActiveAdminAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _auth0Mock.Setup(a => a.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.Admin]));

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.UpdateRolesAsync(userId, [UserRole.PropertyOwner], "auth0|only-admin"));

        Assert.Equal(UserActivationErrors.LastActiveAdmin, ex.Code);
        _auth0Mock.Verify(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRolesAsync_CannotReadCurrentAuth0Roles_ReturnsFailureAndLeavesDbUnchanged()
    {
        var userId = "auth0|user-fail";
        var user = new User { Id = userId, Role = UserRole.PropertyOwner };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _auth0Mock.Setup(a => a.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0UserRolesResult.Failed(Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode)));

        var result = await _service.UpdateRolesAsync(userId, [UserRole.Admin], "auth0|admin");

        Assert.False(result.RoleSync.Succeeded);
        Assert.Equal(Auth0SyncResult.ApiErrorCode, result.RoleSync.ErrorCode);
        Assert.Equal(UserRole.PropertyOwner, user.Role);
        _auth0Mock.Verify(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRolesAsync_SameRolesAsCurrent_NoOpWithoutAuth0WriteCalls()
    {
        var userId = "auth0|unchanged";
        var user = new User { Id = userId, Role = UserRole.PropertyOwner };
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _auth0Mock.Setup(a => a.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner]));

        var result = await _service.UpdateRolesAsync(userId, [UserRole.PropertyOwner], "auth0|admin");

        Assert.True(result.RoleSync.Succeeded);
        Assert.Empty(result.RolesGranted);
        Assert.Empty(result.RolesRevoked);
        _auth0Mock.Verify(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task UpdateRolesAsync_UserNotFound_ThrowsKeyNotFoundException()
    {
        _repoMock.Setup(r => r.GetByIdAsync("nonexistent")).ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateRolesAsync("nonexistent", [UserRole.Admin], "admin"));
    }

    [Fact]
    public async Task UpdateRolesAsync_DeactivatedUser_ThrowsUserInactiveWithoutAuth0Calls()
    {
        var userId = "auth0|user-inactive";
        _repoMock.Setup(r => r.GetByIdAsync(userId))
            .ReturnsAsync(new User { Id = userId, Role = UserRole.PropertyOwner, IsActive = false });

        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.UpdateRolesAsync(userId, [UserRole.Admin], "auth0|admin"));

        Assert.Equal(UserActivationErrors.UserInactive, ex.Code);
        _auth0Mock.Verify(a => a.GetUserRolesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRolesAsync_UserExists_ReturnsAuth0Roles()
    {
        var userId = "auth0|user123";
        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(new User { Id = userId, Role = UserRole.PropertyOwner });
        _auth0Mock.Setup(a => a.GetUserRolesAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner, UserRole.Supplier]));

        var result = await _service.GetRolesAsync(userId);

        Assert.True(result.Sync.Succeeded);
        Assert.Equal([UserRole.PropertyOwner, UserRole.Supplier], result.Roles);
    }

    [Fact]
    public async Task GetRolesAsync_UserNotFound_ThrowsKeyNotFoundException()
    {
        _repoMock.Setup(r => r.GetByIdAsync("nonexistent")).ReturnsAsync((User?)null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.GetRolesAsync("nonexistent"));
    }

    // ─── DeactivateUserAsync / ReactivateUserAsync (PL-03) ──────────────────

    [Fact]
    public async Task DeactivateUserAsync_OwnAccount_ThrowsCannotDeactivateSelfWithoutChanges()
    {
        var ex = await Assert.ThrowsAsync<DomainRuleException>(
            () => _service.DeactivateUserAsync("auth0|admin", "auth0|admin"));

        Assert.Equal(UserActivationErrors.CannotDeactivateSelf, ex.Code);
        _repoMock.Verify(
            r => r.SetActiveAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(UserActivationOutcome.LastActiveAdmin, typeof(DomainRuleException))]
    [InlineData(UserActivationOutcome.ActorInactive, typeof(UnauthorizedAccessException))]
    [InlineData(UserActivationOutcome.NotFound, typeof(NotFoundException))]
    public async Task DeactivateUserAsync_RefusedByTheRepository_ThrowsWithoutAuth0Calls(
        UserActivationOutcome outcome,
        Type expected)
    {
        var user = outcome == UserActivationOutcome.NotFound ? null : new User { Id = "auth0|target", Role = UserRole.Admin };
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", false, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((outcome, user));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _service.DeactivateUserAsync("auth0|target", "auth0|admin"));

        Assert.IsType(expected, ex);
        if (ex is DomainRuleException domain)
            Assert.Equal(UserActivationErrors.LastActiveAdmin, domain.Code);
        _auth0Mock.Verify(a => a.SetBlockedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeactivateUserAsync_ActiveUser_BlocksThenRemembersAndRemovesItsAuth0Roles()
    {
        var user = new User { Id = "auth0|target", Role = UserRole.PropertyOwner, IsActive = false };
        var steps = new List<string>();
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", false, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserActivationOutcome.Updated, user));
        _repoMock.Setup(r => r.UpdateAsync(user))
            .Callback(() => steps.Add("store:" + string.Join(",", user.SuspendedAuth0Roles ?? [])))
            .Returns(Task.CompletedTask);
        _auth0Mock.Setup(a => a.SetBlockedAsync("auth0|target", true, It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("block"))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0Mock.Setup(a => a.GetUserRolesAsync("auth0|target", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner, UserRole.Admin]));
        _auth0Mock.Setup(a => a.RemoveRolesAsync("auth0|target", It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyCollection<UserRole>, CancellationToken>((_, roles, _) => steps.Add("remove:" + string.Join(",", roles)))
            .ReturnsAsync(Auth0SyncResult.Synced);

        var result = await _service.DeactivateUserAsync("auth0|target", "auth0|admin");

        Assert.True(result.Changed);
        Assert.True(result.Auth0Sync.Succeeded);
        // The roles are stored before they are removed: a failure in between never loses them.
        Assert.Equal(["block", "store:PropertyOwner,Admin", "remove:PropertyOwner,Admin"], steps);
        _cacheMock.Verify(c => c.Invalidate("auth0|target"), Times.AtLeastOnce);
    }

    [Fact]
    public async Task DeactivateUserAsync_RetriedOnInactiveUser_KeepsTheRolesRememberedBefore()
    {
        var user = new User { Id = "auth0|target", IsActive = false, SuspendedAuth0Roles = ["Supplier"] };
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", false, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserActivationOutcome.Unchanged, user));
        _auth0Mock.Setup(a => a.SetBlockedAsync("auth0|target", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0Mock.Setup(a => a.GetUserRolesAsync("auth0|target", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Auth0UserRolesResult(Auth0SyncResult.Synced, [UserRole.PropertyOwner]));

        var result = await _service.DeactivateUserAsync("auth0|target", "auth0|admin");

        Assert.False(result.Changed);
        Assert.Equal(["Supplier", "PropertyOwner"], user.SuspendedAuth0Roles);
    }

    [Fact]
    public async Task DeactivateUserAsync_Auth0NotConfigured_KeepsDbDeactivationAndReportsNotConfigured()
    {
        var user = new User { Id = "auth0|target", IsActive = false };
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", false, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserActivationOutcome.Updated, user));
        _auth0Mock.Setup(a => a.SetBlockedAsync("auth0|target", true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.NotConfigured);
        _auth0Mock.Setup(a => a.GetUserRolesAsync("auth0|target", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0UserRolesResult.Failed(Auth0SyncResult.NotConfigured));

        var result = await _service.DeactivateUserAsync("auth0|target", "auth0|admin");

        Assert.True(result.Changed);
        Assert.Equal(Auth0SyncResult.NotConfiguredCode, result.Auth0Sync.ErrorCode);
        Assert.Null(user.SuspendedAuth0Roles);
        _auth0Mock.Verify(a => a.RemoveRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReactivateUserAsync_SuspendedRoles_AssignsThemThenUnblocksAndForgetsThem()
    {
        var user = new User { Id = "auth0|target", IsActive = true, SuspendedAuth0Roles = ["Admin", "Supplier"] };
        var steps = new List<string>();
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", true, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserActivationOutcome.Updated, user));
        _auth0Mock.Setup(a => a.AssignRolesAsync("auth0|target", It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyCollection<UserRole>, CancellationToken>((_, roles, _) => steps.Add("assign:" + string.Join(",", roles)))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0Mock.Setup(a => a.SetBlockedAsync("auth0|target", false, It.IsAny<CancellationToken>()))
            .Callback(() => steps.Add("unblock"))
            .ReturnsAsync(Auth0SyncResult.Synced);

        var result = await _service.ReactivateUserAsync("auth0|target", "auth0|admin");

        Assert.True(result.Auth0Sync.Succeeded);
        Assert.Equal([UserRole.Admin, UserRole.Supplier], result.RestoredRoles);
        Assert.Equal(["assign:Admin,Supplier", "unblock"], steps);
        Assert.Null(user.SuspendedAuth0Roles);
        _repoMock.Verify(r => r.UpdateAsync(user), Times.Once);
    }

    [Fact]
    public async Task ReactivateUserAsync_RolesCannotBeRestored_StaysBlockedAndKeepsThemForTheRetry()
    {
        var user = new User { Id = "auth0|target", IsActive = true, SuspendedAuth0Roles = ["PropertyOwner"] };
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", true, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserActivationOutcome.Updated, user));
        _auth0Mock.Setup(a => a.AssignRolesAsync("auth0|target", It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode));

        var result = await _service.ReactivateUserAsync("auth0|target", "auth0|admin");

        Assert.Equal(Auth0SyncResult.ApiErrorCode, result.Auth0Sync.ErrorCode);
        Assert.Empty(result.RestoredRoles);
        Assert.Equal(["PropertyOwner"], user.SuspendedAuth0Roles);
        _auth0Mock.Verify(a => a.SetBlockedAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        _repoMock.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
    }

    [Fact]
    public async Task ReactivateUserAsync_NoRoleWasRemoved_OnlyUnblocks()
    {
        var user = new User { Id = "auth0|target", IsActive = true };
        _repoMock.Setup(r => r.SetActiveAsync("auth0|target", true, "auth0|admin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((UserActivationOutcome.Updated, user));
        _auth0Mock.Setup(a => a.SetBlockedAsync("auth0|target", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Synced);

        var result = await _service.ReactivateUserAsync("auth0|target", "auth0|admin");

        Assert.True(result.Auth0Sync.Succeeded);
        _auth0Mock.Verify(a => a.AssignRolesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _auth0Mock.Verify(a => a.SetBlockedAsync("auth0|target", false, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── CompleteOnboardingAsync ────────────────────────────────────────────

    [Fact]
    public async Task CompleteOnboardingAsync_ShortTerm_AddsRoleAndRemovesOnlyUnselectedOnboardingRole()
    {
        var sub = "auth0|onboard1";
        var user = new User { Id = sub, Email = "a@b.com", FirstName = "A", LastName = "B" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, "a@b.com", "A B", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "A B" });

        var (result, roles, roleSync) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, "a@b.com", "A", "B");

        Assert.Equal(RentalType.ShortTerm, result.RentalType);
        Assert.Equal(UserRole.PropertyOwner, result.Role);
        Assert.Single(roles);
        Assert.Equal("PropertyOwner", roles[0]);
        Assert.True(roleSync.Succeeded);
        _auth0Mock.Verify(a => a.AssignRolesAsync(
            sub,
            It.Is<IReadOnlyCollection<UserRole>>(list => list.Count == 1 && list.Contains(UserRole.PropertyOwner)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        // Admin / Supplier roles are never part of an onboarding removal.
        _auth0Mock.Verify(a => a.RemoveRolesAsync(
            sub,
            It.Is<IReadOnlyCollection<UserRole>>(list => list.Count == 1 && list.Contains(UserRole.LongTermLandlord)),
            It.IsAny<CancellationToken>()),
            Times.Once);
        _auth0Mock.Verify(a => a.RemoveRoleAsync(It.IsAny<string>(), It.IsAny<UserRole>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CompleteOnboardingAsync_Both_GrantsMembershipsForAllRoles()
    {
        var sub = "auth0|onboard-both";
        var user = new User { Id = sub, Email = "both@b.com", FirstName = "Bo", LastName = "Th" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Bo Th" });

        var (_, roles, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.Both, "both@b.com", "Bo", "Th");

        Assert.Equal(["PropertyOwner", "LongTermLandlord"], roles);
        _membershipMock.Verify(m => m.GrantAsync(
            sub,
            It.Is<IEnumerable<UserRole>>(granted =>
                granted.Contains(UserRole.PropertyOwner) && granted.Contains(UserRole.LongTermLandlord)),
            It.IsAny<CancellationToken>()), Times.Once);
        _membershipMock.Verify(m => m.RevokeAsync(
            sub,
            It.Is<IEnumerable<UserRole>>(revoked => !revoked.Any()),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteOnboardingAsync_SwitchToLongTerm_RevokesShortRentMembership()
    {
        var sub = "auth0|onboard-switch";
        var user = new User
        {
            Id = sub,
            Email = "switch@b.com",
            FirstName = "Sw",
            LastName = "Itch",
            Role = UserRole.PropertyOwner,
            RentalType = RentalType.ShortTerm,
            OnboardingCompletedAt = DateTime.UtcNow.AddDays(-3),
        };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Sw" });

        await _service.CompleteOnboardingAsync(sub, RentalType.LongTerm, "switch@b.com", "Sw", "Itch");

        _membershipMock.Verify(m => m.RevokeAsync(
            sub,
            It.Is<IEnumerable<UserRole>>(revoked => revoked.SequenceEqual(new[] { UserRole.PropertyOwner })),
            It.IsAny<CancellationToken>()), Times.Once);
        _auth0Mock.Verify(a => a.RemoveRolesAsync(
            sub,
            It.Is<IReadOnlyCollection<UserRole>>(list => list.Count == 1 && list.Contains(UserRole.PropertyOwner)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteOnboardingAsync_PlatformAdminSetsUpHostOrg_KeepsAdminRoleAndAddsHostRole()
    {
        var sub = "auth0|onboard-admin";
        var user = new User { Id = sub, Email = "admin@b.com", FirstName = "Ad", LastName = "Min", Role = UserRole.Admin };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Ad Min" });

        var (result, roles, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, "admin@b.com", "Ad", "Min");

        Assert.Equal(UserRole.Admin, result.Role);
        Assert.Equal(RentalType.ShortTerm, result.RentalType);
        Assert.Equal(["PropertyOwner"], roles);
        _auth0Mock.Verify(a => a.RemoveRolesAsync(
            sub,
            It.Is<IReadOnlyCollection<UserRole>>(list => !list.Contains(UserRole.Admin)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteOnboardingAsync_Auth0Fails_ReturnsFailedRoleSyncAndKeepsDbChanges()
    {
        var sub = "auth0|onboard-auth0-down";
        var user = new User { Id = sub, Email = "down@b.com", FirstName = "Do", LastName = "Wn" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Do" });
        _auth0Mock.Setup(a => a.AssignRolesAsync(sub, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.TokenFailedCode));

        var (result, _, roleSync) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, "down@b.com", "Do", "Wn");

        Assert.False(roleSync.Succeeded);
        Assert.Equal(Auth0SyncResult.TokenFailedCode, roleSync.ErrorCode);
        Assert.Equal(RentalType.ShortTerm, result.RentalType);
        Assert.NotNull(result.OnboardingCompletedAt);
        _membershipMock.Verify(m => m.GrantAsync(
            sub,
            It.Is<IEnumerable<UserRole>>(granted => granted.Contains(UserRole.PropertyOwner)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteOnboardingAsync_SetsOnboardingCompletedAtTimestamp()
    {
        // Arrange
        var sub = "auth0|onboard2";
        var user = new User { Id = sub, Email = "c@d.com", FirstName = "C", LastName = "D", OnboardingCompletedAt = null };
        var beforeCall = DateTime.UtcNow;

        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>()))
            .Callback<User>(u => { /* capture updated user */ })
            .Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, "c@d.com", "C D", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "C D" });

        // Act
        var (result, _, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, "c@d.com", "C", "D");

        var afterCall = DateTime.UtcNow;

        // Assert
        Assert.NotNull(result.OnboardingCompletedAt);
        Assert.True(result.OnboardingCompletedAt >= beforeCall && result.OnboardingCompletedAt <= afterCall);
    }

    [Fact]
    public async Task CompleteOnboardingAsync_DoesNotOverwriteExistingTimestamp()
    {
        // Arrange
        var sub = "auth0|onboard3";
        var existingTimestamp = DateTime.UtcNow.AddDays(-1);
        var user = new User { Id = sub, Email = "e@f.com", FirstName = "E", LastName = "F", OnboardingCompletedAt = existingTimestamp };

        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, "e@f.com", "E F", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "E F" });

        // Act
        var (result, _, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, "e@f.com", "E", "F");

        // Assert
        Assert.Equal(existingTimestamp, result.OnboardingCompletedAt);
    }

    // ─── GetPagedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetPagedAsync_DelegatesToRepository()
    {
        // Arrange
        var users = new List<User> { new() { Id = "u1" }, new() { Id = "u2" } };
        _repoMock.Setup(r => r.GetPagedAsync("mario", "PropertyOwner", true, 1, 20))
                 .ReturnsAsync((users, 2));

        // Act
        var (result, count) = await _service.GetPagedAsync("mario", "PropertyOwner", true, 1, 20);

        // Assert
        Assert.Equal(2, count);
        Assert.Equal(2, result.Count());
    }
}
