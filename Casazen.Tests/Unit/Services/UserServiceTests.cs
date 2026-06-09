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
    private readonly Mock<ILogger<UserService>> _loggerMock;
    private readonly UserService _service;

    public UserServiceTests()
    {
        _repoMock = new Mock<IUserRepository>();
        _loggerMock = new Mock<ILogger<UserService>>();
        _auth0Mock = new Mock<IAuth0ManagementService>();
        _orgMock = new Mock<IOrgService>();

        _service = new UserService(_repoMock.Object, _auth0Mock.Object, _orgMock.Object, _loggerMock.Object);
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
    public async Task ChangeRoleAsync_UserExists_UpdatesRoleAndCallsAuth0()
    {
        // Arrange
        var userId = "auth0|user789";
        var adminSub = "auth0|admin000";
        var user = new User { Id = userId, Email = "user@example.com", Role = UserRole.PropertyOwner };

        _repoMock.Setup(r => r.GetByIdAsync(userId)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _auth0Mock.Setup(a => a.AssignRoleAsync(userId, UserRole.Admin)).Returns(Task.CompletedTask);

        // Act
        await _service.ChangeRoleAsync(userId, UserRole.Admin, adminSub);

        // Assert
        Assert.Equal(UserRole.Admin, user.Role);
        _repoMock.Verify(r => r.UpdateAsync(user), Times.Once);
        _auth0Mock.Verify(a => a.AssignRoleAsync(userId, UserRole.Admin), Times.Once);
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
    public async Task CompleteOnboardingAsync_ShortTerm_UpdatesUserAndCallsAuth0()
    {
        var sub = "auth0|onboard1";
        var user = new User { Id = sub, Email = "a@b.com", FirstName = "A", LastName = "B" };
        _repoMock.Setup(r => r.GetBySubAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.GetByIdAsync(sub)).ReturnsAsync(user);
        _repoMock.Setup(r => r.UpdateAsync(It.IsAny<User>())).Returns(Task.CompletedTask);
        _orgMock.Setup(o => o.EnsureOrgForUserAsync(
                sub, "a@b.com", "A B", PlanTier.Pro, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Org { Id = Guid.NewGuid(), PlanTier = PlanTier.Pro, Name = "A B" });
        _auth0Mock.Setup(a => a.AssignOnboardingRolesAsync(sub, It.IsAny<IReadOnlyList<UserRole>>()))
            .Returns(Task.CompletedTask);

        var (result, roles) = await _service.CompleteOnboardingAsync(
            sub, RentalType.ShortTerm, PlanTier.Pro, "a@b.com", "A", "B");

        Assert.Equal(RentalType.ShortTerm, result.RentalType);
        Assert.Equal(UserRole.PropertyOwner, result.Role);
        Assert.Single(roles);
        Assert.Equal("PropertyOwner", roles[0]);
        _auth0Mock.Verify(a => a.AssignOnboardingRolesAsync(
            sub, It.Is<IReadOnlyList<UserRole>>(list => list.Count == 1 && list[0] == UserRole.PropertyOwner)),
            Times.Once);
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
