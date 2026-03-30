using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Controllers;

public class PropertiesControllerTests
{
    private readonly Mock<IPropertyService> _mockService;
    private readonly Mock<ILogger<PropertiesController>> _mockLogger;
    private readonly PropertiesController _controller;

    public PropertiesControllerTests()
    {
        _mockService = new Mock<IPropertyService>();
        _mockLogger = new Mock<ILogger<PropertiesController>>();
        _controller = new PropertiesController(_mockService.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task GetAll_WithAuthenticatedUser_ReturnsUserProperties()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);

        var properties = new List<Property>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", OwnerId = userId },
            new() { Id = Guid.NewGuid(), Name = "Property 2", OwnerId = userId }
        };
        _mockService.Setup(x => x.GetOwnerPropertiesAsync(userId)).ReturnsAsync(properties);

        // Act
        var result = await _controller.GetAll();

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedProperties = Assert.IsAssignableFrom<IEnumerable<Property>>(okResult.Value);
        Assert.Equal(2, returnedProperties.Count());
        _mockService.Verify(x => x.GetOwnerPropertiesAsync(userId), Times.Once);
    }

    [Fact]
    public async Task GetAll_WithoutSubClaim_ReturnsUnauthorized()
    {
        // Arrange - User without "sub" claim
        var claims = new List<Claim>();
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        // Act
        var result = await _controller.GetAll();

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
        _mockService.Verify(x => x.GetOwnerPropertiesAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetById_WithValidId_ReturnsProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property { Id = propertyId, Name = "Test Property" };
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _controller.GetById(propertyId);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedProperty = Assert.IsType<Property>(okResult.Value);
        Assert.Equal(propertyId, returnedProperty.Id);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task GetById_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.GetById(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result.Result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task Create_WithAuthenticatedUser_CreatesPropertyWithCorrectOwnerId()
    {
        // Arrange
        var userId = "auth0|test_user_123";
        SetupUserClaims(userId);

        var inputProperty = new Property
        {
            Name = "New Property",
            City = "Rome",
            Address = "Via Roma 1"
        };

        var createdProperty = new Property
        {
            Id = Guid.NewGuid(),
            Name = "New Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = userId
        };

        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(createdProperty);

        // Act
        var result = await _controller.Create(inputProperty);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var returnedProperty = Assert.IsType<Property>(createdResult.Value);
        Assert.Equal(userId, returnedProperty.OwnerId);
        Assert.Equal("New Property", returnedProperty.Name);

        _mockService.Verify(x => x.CreatePropertyAsync(It.Is<Property>(
            p => p.OwnerId == userId)), Times.Once);
    }

    [Fact]
    public async Task Create_WithoutSubClaim_ReturnsUnauthorized()
    {
        // Arrange - User without "sub" claim
        var claims = new List<Claim>();
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };

        var property = new Property { Name = "New Property" };

        // Act
        var result = await _controller.Create(property);

        // Assert
        Assert.IsType<UnauthorizedResult>(result.Result);
        _mockService.Verify(x => x.CreatePropertyAsync(It.IsAny<Property>()), Times.Never);
    }

    [Theory]
    [InlineData("auth0|123")]
    [InlineData("google-oauth2|1234567890")]
    [InlineData("auth0|very-long-user-id-with-many-characters-1234567890")]
    [InlineData("github|9876543210")]
    public async Task Create_WithVariousAuth0SubFormats_SetsCorrectOwnerId(string userId)
    {
        // Arrange
        SetupUserClaims(userId);

        var inputProperty = new Property
        {
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1"
        };

        var createdProperty = new Property
        {
            Id = Guid.NewGuid(),
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = userId
        };

        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(createdProperty);

        // Act
        var result = await _controller.Create(inputProperty);

        // Assert
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var returnedProperty = Assert.IsType<Property>(createdResult.Value);
        Assert.Equal(userId, returnedProperty.OwnerId);

        _mockService.Verify(x => x.CreatePropertyAsync(It.Is<Property>(
            p => p.OwnerId == userId)), Times.Once);
    }

    [Fact]
    public async Task Update_WithValidId_UpdatesProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var existingProperty = new Property { Id = propertyId, Name = "Original" };
        var updatedProperty = new Property
        {
            Id = propertyId,
            Name = "Updated",
            City = "Rome",
            Address = "Via Roma 1"
        };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);
        _mockService.Setup(x => x.UpdatePropertyAsync(It.IsAny<Property>()))
            .ReturnsAsync(updatedProperty);

        // Act
        var result = await _controller.Update(propertyId, updatedProperty);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.UpdatePropertyAsync(It.Is<Property>(
            p => p.Id == propertyId)), Times.Once);
    }

    [Fact]
    public async Task Update_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var updatedProperty = new Property { Name = "Updated" };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.Update(propertyId, updatedProperty);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.UpdatePropertyAsync(It.IsAny<Property>()), Times.Never);
    }

    [Fact]
    public async Task Delete_WithValidId_DeletesProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var existingProperty = new Property { Id = propertyId, Name = "To Delete" };

        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync(existingProperty);
        _mockService.Setup(x => x.DeletePropertyAsync(propertyId)).ReturnsAsync(true);

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<NoContentResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.DeletePropertyAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task Delete_WithNonExistentId_ReturnsNotFound()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        _mockService.Setup(x => x.GetPropertyAsync(propertyId)).ReturnsAsync((Property?)null);

        // Act
        var result = await _controller.Delete(propertyId);

        // Assert
        Assert.IsType<NotFoundResult>(result);
        _mockService.Verify(x => x.GetPropertyAsync(propertyId), Times.Once);
        _mockService.Verify(x => x.DeletePropertyAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Search_WithFilters_ReturnsFilteredProperties()
    {
        // Arrange
        var properties = new List<Property>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", City = "Rome", Bedrooms = 2, NightlyRate = 100m },
            new() { Id = Guid.NewGuid(), Name = "Property 2", City = "Rome", Bedrooms = 3, NightlyRate = 150m }
        };

        _mockService.Setup(x => x.SearchAsync("Rome", 2, 200m)).ReturnsAsync(properties);

        // Act
        var result = await _controller.Search("Rome", 2, 200m);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var returnedProperties = Assert.IsAssignableFrom<IEnumerable<Property>>(okResult.Value);
        Assert.Equal(2, returnedProperties.Count());
        _mockService.Verify(x => x.SearchAsync("Rome", 2, 200m), Times.Once);
    }

    [Fact]
    public async Task GetAll_CallsServiceWithCorrectOwnerId()
    {
        // Arrange
        var userId = "auth0|specific_user_id_12345";
        SetupUserClaims(userId);

        _mockService.Setup(x => x.GetOwnerPropertiesAsync(userId))
            .ReturnsAsync(new List<Property>());

        // Act
        await _controller.GetAll();

        // Assert
        _mockService.Verify(x => x.GetOwnerPropertiesAsync(userId), Times.Once);
    }

    [Fact]
    public async Task Create_OverridesOwnerId_WithAuthenticatedUser()
    {
        // Arrange - User tries to create property with different OwnerId
        var authenticatedUserId = "auth0|authenticated_user";
        var attemptedOwnerId = "auth0|different_user";

        SetupUserClaims(authenticatedUserId);

        var inputProperty = new Property
        {
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = attemptedOwnerId // User tries to set different owner
        };

        Property? capturedProperty = null;
        _mockService.Setup(x => x.CreatePropertyAsync(It.IsAny<Property>()))
            .Callback<Property>(p => capturedProperty = p)
            .ReturnsAsync((Property p) => p);

        // Act
        await _controller.Create(inputProperty);

        // Assert - OwnerId should be overridden with authenticated user's ID
        Assert.NotNull(capturedProperty);
        Assert.Equal(authenticatedUserId, capturedProperty.OwnerId);
        Assert.NotEqual(attemptedOwnerId, capturedProperty.OwnerId);
    }

    private void SetupUserClaims(string userId)
    {
        var claims = new List<Claim>
        {
            new Claim("sub", userId)
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var claimsPrincipal = new ClaimsPrincipal(identity);

        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = claimsPrincipal }
        };
    }
}
