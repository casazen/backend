using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The reach check of the controllers not yet moved to the resource handler. Since AM-03 it asks the scope resolver: the org
/// role of a member first, the token only for an account in no org team.
/// </summary>
public class PropertyAuthorizationServiceTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid GivenPropertyId = Guid.NewGuid();

    private readonly Mock<IPropertyRepository> _mockRepository = new();

    private PropertyAuthorizationService ServiceFor(UserAuthorizationSnapshot? snapshot = null) =>
        new(_mockRepository.Object, HostAuthorizationTestHarness.ScopeResolver(snapshot));

    private static UserAuthorizationSnapshot Member(
        OrgRole role,
        PropertyScope scope = PropertyScope.All,
        params Guid[] granted) => new(
        Exists: true,
        IsActive: true,
        Role: UserRole.None,
        SupplierOrgId: null,
        Memberships: [],
        OrgMember: new OrgMemberSnapshot(OrgId, role, OrgMemberStatus.Active, scope, granted.Length == 0 ? null : granted.ToHashSet()));

    private static Property PropertyOf(string ownerId, Guid? id = null) =>
        new() { Id = id ?? Guid.NewGuid(), OwnerId = ownerId, OrgId = OrgId };

    // ─── CanAccessAsync: an account in no org team (the rule of before the team) ───────────────────

    [Fact]
    public async Task CanAccessAsync_WhenUserIsOwner_ReturnsTrue()
    {
        Assert.True(await ServiceFor().CanAccessAsync("auth0|owner", PropertyOf("auth0|owner"), []));
    }

    [Fact]
    public async Task CanAccessAsync_WhenUserIsNotOwnerAndHasNoPrivilegedRole_ReturnsFalse()
    {
        Assert.False(await ServiceFor().CanAccessAsync("auth0|attacker", PropertyOf("auth0|owner"), ["PropertyOwner"]));
    }

    [Theory]
    [InlineData("PropertyManager")]
    [InlineData("Admin")]
    public async Task CanAccessAsync_WhenTheTokenHasAnOrgWideRole_ReturnsTrueRegardlessOfOwnership(string role)
    {
        Assert.True(await ServiceFor().CanAccessAsync("auth0|manager", PropertyOf("auth0|owner"), [role]));
    }

    // ─── CanAccessAsync: a member of the team is decided by its membership (AM-03) ───────────────────

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    [InlineData(OrgRole.PropertyManager)]
    [InlineData(OrgRole.Accountant)]
    public async Task CanAccessAsync_MemberWithAnOrgWideRole_ReachesAPropertyItDidNotCreate_WithoutAnyTokenRole(OrgRole role)
    {
        Assert.True(await ServiceFor(Member(role)).CanAccessAsync("auth0|member", PropertyOf("auth0|creator"), []));
    }

    [Fact]
    public async Task CanAccessAsync_CollaboratorSoloAlcuni_ReachesOnlyTheGivenProperty()
    {
        var service = ServiceFor(Member(OrgRole.Collaborator, PropertyScope.Selected, GivenPropertyId));

        Assert.True(await service.CanAccessAsync("auth0|collab", PropertyOf("auth0|creator", GivenPropertyId), []));
        Assert.False(await service.CanAccessAsync("auth0|collab", PropertyOf("auth0|creator"), []));
    }

    [Fact]
    public async Task CanAccessAsync_CollaboratorSoloAlcuni_IsNotWidenedByAnOrgWideRoleLeftInTheToken()
    {
        var service = ServiceFor(Member(OrgRole.Collaborator, PropertyScope.Selected, GivenPropertyId));

        Assert.False(await service.CanAccessAsync("auth0|collab", PropertyOf("auth0|creator"), ["PropertyManager", "Admin"]));
    }

    // ─── CanAccessPropertyAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CanAccessPropertyAsync_WhenUserIsOwner_ReturnsTrue()
    {
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetRecordAsync(propertyId)).ReturnsAsync(PropertyOf("auth0|owner", propertyId));

        Assert.True(await ServiceFor().CanAccessPropertyAsync("auth0|owner", propertyId, []));
    }

    [Fact]
    public async Task CanAccessPropertyAsync_WhenUserIsNotOwner_ReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetRecordAsync(propertyId)).ReturnsAsync(PropertyOf("auth0|owner", propertyId));

        Assert.False(await ServiceFor().CanAccessPropertyAsync("auth0|attacker", propertyId, []));
    }

    [Theory]
    [InlineData("PropertyManager")]
    [InlineData("Admin")]
    public async Task CanAccessPropertyAsync_WhenPropertyNotFound_ReturnsFalse_EvenForAnOrgWideRole(string role)
    {
        // Not in the caller's org (the tenant filter), or not there at all: nothing to reach.
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetRecordAsync(propertyId)).ReturnsAsync((Property?)null);

        Assert.False(await ServiceFor().CanAccessPropertyAsync("auth0|manager", propertyId, [role]));
        _mockRepository.Verify(x => x.GetRecordAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task CanAccessPropertyAsync_CollaboratorSoloAlcuni_ReachesTheGivenPropertyOnly()
    {
        var other = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetRecordAsync(GivenPropertyId)).ReturnsAsync(PropertyOf("auth0|creator", GivenPropertyId));
        _mockRepository.Setup(x => x.GetRecordAsync(other)).ReturnsAsync(PropertyOf("auth0|creator", other));
        var service = ServiceFor(Member(OrgRole.Collaborator, PropertyScope.Selected, GivenPropertyId));

        Assert.True(await service.CanAccessPropertyAsync("auth0|collab", GivenPropertyId, []));
        Assert.False(await service.CanAccessPropertyAsync("auth0|collab", other, []));
    }
}
