using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class PropertyServiceTests
{
    private readonly Mock<IPropertyRepository> _mockRepository;
    private readonly PropertyService _service;

    public PropertyServiceTests()
    {
        _mockRepository = new Mock<IPropertyRepository>();
        _service = new PropertyService(_mockRepository.Object, new Mock<ILogger<PropertyService>>().Object);
    }

    [Fact]
    public async Task GetPropertyAsync_WithValidId_ReturnsProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var property = new Property { Id = propertyId, Name = "Test Property" };
        _mockRepository.Setup(x => x.GetByIdAsync(propertyId)).ReturnsAsync(property);

        // Act
        var result = await _service.GetPropertyAsync(propertyId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(propertyId, result.Id);
        _mockRepository.Verify(x => x.GetByIdAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task CreatePropertyAsync_WithValidProperty_ReturnsCreatedProperty()
    {
        // Arrange
        var property = new Property { Name = "New Property", City = "Test City" };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Property>())).ReturnsAsync(property);

        // Act
        var result = await _service.CreatePropertyAsync(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(property.Name, result.Name);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Property>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_WithCityFilter_ReturnsFilteredProperties()
    {
        // Arrange
        var properties = new List<Property>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", City = "Rome" },
            new() { Id = Guid.NewGuid(), Name = "Property 2", City = "Rome" }
        };
        _mockRepository.Setup(x => x.SearchAsync("Rome", null, null)).ReturnsAsync(properties);

        // Act
        var result = await _service.SearchAsync("Rome", null, null);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task GetOwnerPropertiesAsync_WithValidOwnerId_ReturnsProperties()
    {
        // Arrange
        var ownerId = "auth0|test_user_123";
        var properties = new List<Property>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", City = "Rome", OwnerId = ownerId },
            new() { Id = Guid.NewGuid(), Name = "Property 2", City = "Milan", OwnerId = ownerId }
        };
        _mockRepository.Setup(x => x.GetByOwnerAsync(ownerId)).ReturnsAsync(properties);

        // Act
        var result = await _service.GetOwnerPropertiesAsync(ownerId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Count());
        Assert.All(result, p => Assert.Equal(ownerId, p.OwnerId));
        _mockRepository.Verify(x => x.GetByOwnerAsync(ownerId), Times.Once);
    }

    [Fact]
    public async Task GetOwnerPropertiesAsync_WithNonExistentOwnerId_ReturnsEmpty()
    {
        // Arrange
        var ownerId = "auth0|nonexistent_user";
        _mockRepository.Setup(x => x.GetByOwnerAsync(ownerId)).ReturnsAsync(new List<Property>());

        // Act
        var result = await _service.GetOwnerPropertiesAsync(ownerId);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
        _mockRepository.Verify(x => x.GetByOwnerAsync(ownerId), Times.Once);
    }

    [Theory]
    [InlineData("auth0|123")]
    [InlineData("google-oauth2|1234567890")]
    [InlineData("auth0|very-long-user-id-with-many-characters-1234567890")]
    public async Task GetOwnerPropertiesAsync_WithVariousAuth0Formats_CallsRepository(string ownerId)
    {
        // Arrange
        var properties = new List<Property>
        {
            new() { Id = Guid.NewGuid(), Name = "Property 1", OwnerId = ownerId }
        };
        _mockRepository.Setup(x => x.GetByOwnerAsync(ownerId)).ReturnsAsync(properties);

        // Act
        var result = await _service.GetOwnerPropertiesAsync(ownerId);

        // Assert
        Assert.Single(result);
        Assert.Equal(ownerId, result.First().OwnerId);
        _mockRepository.Verify(x => x.GetByOwnerAsync(ownerId), Times.Once);
    }

    [Fact]
    public async Task CreatePropertyAsync_WithAuth0OwnerId_CreatesProperty()
    {
        // Arrange
        var ownerId = "auth0|test_user_123";
        var property = new Property
        {
            Name = "New Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = ownerId
        };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Property>())).ReturnsAsync(property);

        // Act
        var result = await _service.CreatePropertyAsync(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ownerId, result.OwnerId);
        Assert.Equal(property.Name, result.Name);
        _mockRepository.Verify(x => x.AddAsync(It.Is<Property>(p => p.OwnerId == ownerId)), Times.Once);
    }

    [Fact]
    public async Task DeletePropertyAsync_WithValidId_DeletesProperty()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        _mockRepository.Setup(x => x.DeleteAsync(propertyId)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.DeletePropertyAsync(propertyId);

        // Assert
        Assert.True(result);
        _mockRepository.Verify(x => x.DeleteAsync(propertyId), Times.Once);
    }

    [Fact]
    public async Task UpdatePropertyAsync_WithValidProperty_UpdatesProperty()
    {
        // Arrange
        var property = new Property
        {
            Id = Guid.NewGuid(),
            Name = "Updated Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = "auth0|test_user_123"
        };
        _mockRepository.Setup(x => x.UpdateAsync(It.IsAny<Property>())).ReturnsAsync(property);

        // Act
        var result = await _service.UpdatePropertyAsync(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(property.Id, result.Id);
        Assert.Equal(property.Name, result.Name);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Property>()), Times.Once);
    }
}
