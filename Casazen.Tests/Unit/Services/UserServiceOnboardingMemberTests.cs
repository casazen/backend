using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-00 (S2): a member of an org (a DB membership of a host context with a role other than the owner's) cannot complete
/// the onboarding, because the grant would overwrite that role and assign <c>PropertyOwner</c> in Auth0: 409
/// <c>member_cannot_onboard</c>, nothing written. The owner (no membership yet, or already the owner's) goes on.
/// </summary>
public class UserServiceOnboardingMemberTests
{
    private const string Sub = "auth0|onboarding-member";
    private const string Email = "member@example.com";

    private readonly Mock<IUserRepository> _repository = new();
    private readonly Mock<IAuth0ManagementService> _auth0 = new(MockBehavior.Strict);
    private readonly Mock<IOrgService> _orgs = new();
    private readonly Mock<IUserContextMembershipService> _memberships = new();
    private readonly Mock<IUserAuthorizationCache> _cache = new();
    private readonly List<string> _calls = [];
    private readonly UserService _service;

    public UserServiceOnboardingMemberTests()
    {
        var user = new User { Id = Sub, Email = Email, FirstName = "Mem", LastName = "Bro" };
        _repository.Setup(r => r.GetBySubAsync(Sub)).ReturnsAsync(user);
        _repository.Setup(r => r.GetByIdAsync(Sub)).ReturnsAsync(user);
        _repository.Setup(r => r.UpdateAsync(It.IsAny<User>()))
            .Callback<User>(_ => _calls.Add("user-updated"))
            .Returns(Task.CompletedTask);
        _orgs.Setup(o => o.EnsureOrgForUserAsync(Sub, Email, "Mem Bro", It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("org-ensured"))
            .ReturnsAsync(new OrgEntity { Id = Guid.NewGuid(), PlanTier = PlanTier.Starter, Name = "Mem Bro" });
        _memberships.Setup(m => m.GrantAsync(Sub, It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("membership-granted"))
            .Returns(Task.CompletedTask);
        _memberships.Setup(m => m.RevokeAsync(Sub, It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("membership-revoked"))
            .Returns(Task.CompletedTask);
        _auth0.Setup(a => a.AssignRolesAsync(Sub, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("auth0-roles-assigned"))
            .ReturnsAsync(Auth0SyncResult.Synced);
        _auth0.Setup(a => a.RemoveRolesAsync(Sub, It.IsAny<IReadOnlyCollection<UserRole>>(), It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("auth0-roles-removed"))
            .ReturnsAsync(Auth0SyncResult.Synced);

        _service = new UserService(
            _repository.Object,
            _auth0.Object,
            _orgs.Object,
            _memberships.Object,
            _cache.Object,
            NullLogger<UserService>.Instance);
    }

    [Theory]
    [InlineData(RentalType.ShortTerm)]
    [InlineData(RentalType.LongTerm)]
    [InlineData(RentalType.Both)]
    public async Task CompleteOnboardingAsync_MemberOfAnOrg_ThrowsMemberCannotOnboardAndWritesNothing(RentalType rentalType)
    {
        _memberships.Setup(m => m.IsHostMemberAsync(Sub, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var error = await Assert.ThrowsAsync<DomainConflictException>(
            () => _service.CompleteOnboardingAsync(Sub, rentalType, Email, "Mem", "Bro"));

        Assert.Equal("member_cannot_onboard", error.Code);
        Assert.Equal(UserOnboardingErrors.MemberCannotOnboard, error.Code);
        Assert.Equal("MemberCannotOnboard", error.MessageKey);

        // Not a single write: no user row touched or created, no org provisioned, no membership overwritten, no Auth0 call.
        Assert.Empty(_calls);
        _repository.Verify(r => r.GetBySubAsync(It.IsAny<string>()), Times.Never);
        _repository.Verify(r => r.AddIfAbsentAsync(It.IsAny<User>()), Times.Never);
        _repository.Verify(r => r.UpdateAsync(It.IsAny<User>()), Times.Never);
        _orgs.VerifyNoOtherCalls();
        _memberships.Verify(m => m.GrantAsync(It.IsAny<string>(), It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _memberships.Verify(m => m.RevokeAsync(It.IsAny<string>(), It.IsAny<IEnumerable<UserRole>>(), It.IsAny<CancellationToken>()), Times.Never);
        _auth0.VerifyNoOtherCalls();
        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(RentalType.ShortTerm, "PropertyOwner")]
    [InlineData(RentalType.LongTerm, "LongTermLandlord")]
    [InlineData(RentalType.Both, "PropertyOwner,LongTermLandlord")]
    public async Task CompleteOnboardingAsync_NotAMember_ChecksFirstThenOnboardsAsBefore(RentalType rentalType, string expectedRoles)
    {
        // The owner of an org (no membership yet, or already the owner's) and a platform admin (its admin membership is not
        // a host context) are not members: IsHostMemberAsync answers false.
        _memberships.Setup(m => m.IsHostMemberAsync(Sub, It.IsAny<CancellationToken>()))
            .Callback(() => _calls.Add("member-checked"))
            .ReturnsAsync(false);

        var (_, roles, roleSync) = await _service.CompleteOnboardingAsync(Sub, rentalType, Email, "Mem", "Bro");

        Assert.Equal(expectedRoles.Split(','), roles);
        Assert.True(roleSync.Succeeded);
        Assert.Equal("member-checked", _calls[0]);
        Assert.Contains("membership-granted", _calls);
        Assert.Contains("auth0-roles-assigned", _calls);
        _memberships.Verify(m => m.IsHostMemberAsync(Sub, It.IsAny<CancellationToken>()), Times.Once);
    }
}
