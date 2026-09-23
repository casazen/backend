using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
        _repoMock.Verify(r => r.AddAsync(It.IsAny<User>()), Times.Never);
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
        _repoMock.Setup(r => r.AddAsync(It.IsAny<User>()))
                 .ReturnsAsync((User u) => u);

        // Act
        var result = await _service.GetCurrentUserAsync(sub, "new@example.com", "Luigi", "Verdi");

        // Assert
        Assert.Equal(sub, result.Id);
        Assert.Equal("new@example.com", result.Email);
        Assert.Equal("Luigi", result.FirstName);
        Assert.True(result.IsActive);
        _repoMock.Verify(r => r.AddAsync(It.Is<User>(u => u.Id == sub)), Times.Once);
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
                sub, "a@b.com", "A B", PlanTier.Pro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Pro, Name = "A B" });

        var (result, roles, roleSync) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, PlanTier.Pro, "a@b.com", "A", "B");

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
                sub, It.IsAny<string>(), It.IsAny<string>(), PlanTier.Starter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Bo Th" });

        var (_, roles, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.Both, PlanTier.Starter, "both@b.com", "Bo", "Th");

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
                sub, It.IsAny<string>(), It.IsAny<string>(), PlanTier.Starter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Sw" });

        await _service.CompleteOnboardingAsync(sub, RentalType.LongTerm, PlanTier.Starter, "switch@b.com", "Sw", "Itch");

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
    public async Task CompleteOnboardingAsync_Auth0Fails_ReturnsFailedRoleSyncAndKeepsDbChanges()
    {
        var sub = "auth0|onboard-auth0-down";
        var user = new User { Id = sub, Email = "down@b.com", FirstName = "Do", LastName = "Wn" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, It.IsAny<string>(), It.IsAny<string>(), PlanTier.Starter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Do" });
        _auth0Mock.Setup(a => a.AssignRolesAsync(sub, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Auth0SyncResult.Failed(Auth0SyncResult.TokenFailedCode));

        var (result, _, roleSync) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, PlanTier.Starter, "down@b.com", "Do", "Wn");

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
                sub, "c@d.com", "C D", PlanTier.Starter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "C D" });

        // Act
        var (result, _, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, PlanTier.Starter, "c@d.com", "C", "D");

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
                sub, "e@f.com", "E F", PlanTier.Starter, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "E F" });

        // Act
        var (result, _, _) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, PlanTier.Starter, "e@f.com", "E", "F");

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
