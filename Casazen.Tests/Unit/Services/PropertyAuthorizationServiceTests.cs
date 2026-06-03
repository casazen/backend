using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyAuthorizationServiceTests
{
    private readonly Mock<IPropertyRepository> _mockRepository;
    private readonly PropertyAuthorizationService _service;

    public PropertyAuthorizationServiceTests()
    {
        _mockRepository = new Mock<IPropertyRepository>();
        _service = new PropertyAuthorizationService(_mockRepository.Object);
    }

    // ─── CanAccess (sync) ────────────────────────────────────────────────────────

    [Fact]
    public void CanAccess_WhenUserIsOwner_ReturnsTrue()
    {
        var result = _service.CanAccess("auth0|owner", "auth0|owner", []);
        Assert.True(result);
    }

    [Fact]
    public void CanAccess_WhenUserIsNotOwnerAndHasNoPrivilegedRole_ReturnsFalse()
    {
        var result = _service.CanAccess("auth0|attacker", "auth0|owner", ["PropertyOwner"]);
        Assert.False(result);
    }

    [Fact]
    public void CanAccess_WhenUserHasPropertyManagerRole_ReturnsTrueRegardlessOfOwnership()
    {
        var result = _service.CanAccess("auth0|manager", "auth0|owner", ["PropertyManager"]);
        Assert.True(result);
    }

    [Fact]
    public void CanAccess_WhenUserHasAdminRole_ReturnsTrueRegardlessOfOwnership()
    {
        var result = _service.CanAccess("auth0|admin", "auth0|owner", ["Admin"]);
        Assert.True(result);
    }

    // ─── CanAccessPropertyAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task CanAccessPropertyAsync_WhenUserIsOwner_ReturnsTrue()
    {
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetByIdAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = "auth0|owner" });

        var result = await _service.CanAccessPropertyAsync("auth0|owner", propertyId, []);

        Assert.True(result);
    }

    [Fact]
    public async Task CanAccessPropertyAsync_WhenUserIsNotOwner_ReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetByIdAsync(propertyId))
            .ReturnsAsync(new Property { Id = propertyId, OwnerId = "auth0|owner" });

        var result = await _service.CanAccessPropertyAsync("auth0|attacker", propertyId, []);

        Assert.False(result);
    }

    [Fact]
    public async Task CanAccessPropertyAsync_WhenPropertyNotFound_ReturnsFalse()
    {
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync((Property?)null);

        var result = await _service.CanAccessPropertyAsync("auth0|user", propertyId, []);

        Assert.False(result);
        _mockRepository.Verify(x => x.GetByIdAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task CanAccessPropertyAsync_WhenUserHasPropertyManagerRole_ReturnsTrueWithoutFetchingProperty()
    {
        var propertyId = Guid.NewGuid();

        var result = await _service.CanAccessPropertyAsync("auth0|manager", propertyId, ["PropertyManager"]);

        Assert.True(result);
        _mockRepository.Verify(x => x.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }
}
