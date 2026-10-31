using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The deletion of an account by the guest it belongs to (the stores' «delete account in the app»,
/// <see cref="IUserService.DeleteOwnGuestAccountAsync"/>): Auth0 first and nothing removed when it refuses, the CasaZen row after, and
/// no account that is more than a guest's, whatever it asks. Mocks of the repository and of Auth0: the order of the calls is the point.
/// </summary>
public class UserServiceGuestAccountDeletionTests
{
    private const string UserId = "auth0|guest-delete";

    private readonly Mock<IUserRepository> _repository = new(MockBehavior.Strict);
    private readonly Mock<IAuth0ManagementService> _auth0 = new(MockBehavior.Strict);
    private readonly Mock<IUserAuthorizationCache> _cache = new(MockBehavior.Strict);
    private readonly List<string> _calls = [];
    private readonly UserService _service;

    public UserServiceGuestAccountDeletionTests()
    {
        _service = new UserService(
            _repository.Object,
            _auth0.Object,
            Mock.Of<IOrgService>(MockBehavior.Strict),
            Mock.Of<IUserContextMembershipService>(MockBehavior.Strict),
            Mock.Of<IOrgMembershipService>(MockBehavior.Strict),
            _cache.Object,
            NullLogger<UserService>.Instance);
    }

    private static User Guest(
        UserRole role = UserRole.None,
        Guid? orgId = null,
        Guid? supplierOrgId = null) =>
        new() { Id = UserId, Email = "guest@example.com", Role = role, OrgId = orgId, SupplierOrgId = supplierOrgId };

    private void GivenUser(User? user) =>
        _repository.Setup(r => r.GetByIdAsync(UserId)).ReturnsAsync(user);

    private void GivenAuth0Answer(Auth0SyncResult result) =>
        _auth0
            .Setup(a => a.DeleteUserAsync(UserId, It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("auth0"))
            .ReturnsAsync(result);

    private void GivenRowDeletion(Func<Task>? behavior = null) =>
        _repository
            .Setup(r => r.DeleteAsync(UserId, It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("row"))
            .Returns(behavior ?? (() => Task.CompletedTask));

    [Theory]
    [InlineData(UserRole.None)]
    [InlineData(UserRole.Guest)]
    public async Task DeleteOwnGuestAccountAsync_AccountOfAGuest_DeletesAuth0FirstThenTheRowThenDropsTheCache(UserRole role)
    {
        GivenUser(Guest(role));
        GivenAuth0Answer(Auth0SyncResult.Synced);
        GivenRowDeletion();
        _cache.Setup(c => c.Invalidate(UserId)).Callback(() => _calls.Add("cache"));

        var result = await _service.DeleteOwnGuestAccountAsync(UserId);

        Assert.True(result.Succeeded);
        Assert.Equal(["auth0", "row", "cache"], _calls);
        _repository.Verify(r => r.DeleteAsync(UserId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(Auth0SyncResult.RateLimitedCode)]
    [InlineData(Auth0SyncResult.ApiErrorCode)]
    [InlineData(Auth0SyncResult.TokenFailedCode)]
    public async Task DeleteOwnGuestAccountAsync_Auth0Refuses_ReturnsTheFailureAndRemovesNothing(string errorCode)
    {
        GivenUser(Guest());
        GivenAuth0Answer(Auth0SyncResult.Failed(errorCode));

        var result = await _service.DeleteOwnGuestAccountAsync(UserId);

        Assert.False(result.Succeeded);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(["auth0"], _calls);
        _repository.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteOwnGuestAccountAsync_Auth0NotConfigured_ReturnsTheFailureAndRemovesNothing()
    {
        GivenUser(Guest());
        GivenAuth0Answer(Auth0SyncResult.NotConfigured);

        var result = await _service.DeleteOwnGuestAccountAsync(UserId);

        Assert.False(result.Succeeded);
        Assert.Equal(Auth0SyncResult.NotConfiguredCode, result.ErrorCode);
        _repository.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public static TheoryData<string, UserRole, bool, bool> NotOnlyAGuest => new()
    {
        { "host org", UserRole.None, true, false },
        { "supplier link", UserRole.None, false, true },
        { "host org and supplier link", UserRole.None, true, true },
        { "property owner role", UserRole.PropertyOwner, false, false },
        { "long-term landlord role", UserRole.LongTermLandlord, false, false },
        { "supplier role", UserRole.Supplier, false, false },
        { "staff role", UserRole.Staff, false, false },
        { "property manager role", UserRole.PropertyManager, false, false },
        { "platform administrator", UserRole.Admin, false, false },
    };

    [Theory]
    [MemberData(nameof(NotOnlyAGuest))]
    public async Task DeleteOwnGuestAccountAsync_AccountThatIsMoreThanAGuests_ConflictsBeforeAnyCallToAuth0(
        string why,
        UserRole role,
        bool hasHostOrg,
        bool hasSupplierLink)
    {
        GivenUser(Guest(role, hasHostOrg ? Guid.NewGuid() : null, hasSupplierLink ? Guid.NewGuid() : null));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => _service.DeleteOwnGuestAccountAsync(UserId));

        Assert.True(conflict.Code == GuestAccountErrorCodes.IsProfessional, why);
        Assert.Equal("guest_account_is_professional", conflict.Code);
        Assert.Equal("GuestAccountIsProfessional", conflict.MessageKey);
        _auth0.Verify(a => a.DeleteUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _repository.Verify(r => r.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteOwnGuestAccountAsync_ARowRefusesTheDelete_ConflictsAfterAuth0AndKeepsTheRowForTheNextTry()
    {
        GivenUser(Guest());
        GivenAuth0Answer(Auth0SyncResult.Synced);
        GivenRowDeletion(() => throw new DbUpdateException("a restricting foreign key"));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(() => _service.DeleteOwnGuestAccountAsync(UserId));

        Assert.Equal(UserActivationErrors.DeleteBlocked, conflict.Code);
        Assert.Equal("GuestAccountDeleteBlocked", conflict.MessageKey);
        Assert.Equal(["auth0", "row"], _calls);
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task DeleteOwnGuestAccountAsync_NoSuchUser_NotFound()
    {
        GivenUser(null);

        await Assert.ThrowsAsync<NotFoundException>(() => _service.DeleteOwnGuestAccountAsync(UserId));

        _auth0.Verify(a => a.DeleteUserAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task DeleteOwnGuestAccountAsync_BlankId_IsRefused(string userId)
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _service.DeleteOwnGuestAccountAsync(userId));
    }
}
