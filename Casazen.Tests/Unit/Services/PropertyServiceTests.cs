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
}
