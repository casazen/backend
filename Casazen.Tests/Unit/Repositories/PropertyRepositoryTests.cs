using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

public class PropertyRepositoryTests
{
    private readonly AppDbContext _context;
    private readonly PropertyRepository _repository;

    public PropertyRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _repository = new PropertyRepository(_context);
    }

    [Fact]
    public async Task AddAsync_WithValidProperty_AddsProperty()
    {
        // Arrange
        var property = new Property
        {
            Name = "Test Property",
            City = "Test City",
            Address = "Test Address",
            OwnerId = "auth0|test_user_123"
        };

        // Act
        var result = await _repository.AddAsync(property);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        var savedProperty = await _context.Properties.FindAsync(result.Id);
        Assert.NotNull(savedProperty);
    }

    [Fact]
    public async Task SearchAsync_WithCityFilter_ReturnsMatchingProperties()
    {
        // Arrange
        await _repository.AddAsync(new Property
        {
            Name = "Rome Property",
            City = "Rome",
            Address = "Via Roma",
            OwnerId = "auth0|test_user_123"
        });

        await _repository.AddAsync(new Property
        {
            Name = "Milan Property",
            City = "Milan",
            Address = "Via Milano",
            OwnerId = "auth0|test_user_123"
        });

        // Act
        var result = await _repository.SearchAsync("Rome", null, null);

        // Assert
        Assert.Single(result);
        Assert.Equal("Rome Property", result.First().Name);
    }

    [Fact]
    public async Task GetByOwnerAsync_WithValidOwnerId_ReturnsOwnerProperties()
    {
        // Arrange
        var ownerId = "auth0|test_user_123";
        var otherOwnerId = "auth0|test_user_456";

        await _repository.AddAsync(new Property
        {
            Name = "Property 1",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = ownerId
        });

        await _repository.AddAsync(new Property
        {
            Name = "Property 2",
            City = "Milan",
            Address = "Via Milano 1",
            OwnerId = ownerId
        });

        await _repository.AddAsync(new Property
        {
            Name = "Other Property",
            City = "Florence",
            Address = "Via Firenze 1",
            OwnerId = otherOwnerId
        });

        // Act
        var result = await _repository.GetByOwnerAsync(ownerId);

        // Assert
        Assert.Equal(2, result.Count());
        Assert.All(result, p => Assert.Equal(ownerId, p.OwnerId));
    }

    [Fact]
    public async Task GetByOwnerAsync_WithNonExistentOwnerId_ReturnsEmpty()
    {
        // Arrange
        var ownerId = "auth0|nonexistent_user";

        // Act
        var result = await _repository.GetByOwnerAsync(ownerId);

        // Assert
        Assert.Empty(result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auth0|123")]
    [InlineData("google-oauth2|1234567890")]
    [InlineData("auth0|very-long-user-id-with-many-characters-1234567890")]
    public async Task AddAsync_WithVariousOwnerIdFormats_SuccessfullyAddsProperty(string ownerId)
    {
        // Arrange
        var property = new Property
        {
            Name = "Test Property",
            City = "Test City",
            Address = "Test Address",
            OwnerId = ownerId
        };

        // Act
        var result = await _repository.AddAsync(property);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(ownerId, result.OwnerId);
    }

    [Fact]
    public async Task GetByOwnerAsync_WithInactiveProperties_ExcludesInactive()
    {
        // Arrange
        var ownerId = "auth0|test_user_123";

        await _repository.AddAsync(new Property
        {
            Name = "Active Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = ownerId,
            IsActive = true
        });

        await _repository.AddAsync(new Property
        {
            Name = "Inactive Property",
            City = "Milan",
            Address = "Via Milano 1",
            OwnerId = ownerId,
            IsActive = false
        });

        // Act
        var result = await _repository.GetByOwnerAsync(ownerId);

        // Assert
        Assert.Single(result);
        Assert.Equal("Active Property", result.First().Name);
        Assert.True(result.First().IsActive);
    }

    [Fact]
    public async Task GetByOwnerAsync_WithMultipleOwners_ReturnsOnlyRequestedOwnerProperties()
    {
        // Arrange
        var owner1 = "auth0|owner_1";
        var owner2 = "google-oauth2|owner_2";
        var owner3 = "auth0|owner_3";

        await _repository.AddAsync(new Property { Name = "Property 1", City = "Rome", Address = "Address 1", OwnerId = owner1 });
        await _repository.AddAsync(new Property { Name = "Property 2", City = "Rome", Address = "Address 2", OwnerId = owner2 });
        await _repository.AddAsync(new Property { Name = "Property 3", City = "Rome", Address = "Address 3", OwnerId = owner1 });
        await _repository.AddAsync(new Property { Name = "Property 4", City = "Rome", Address = "Address 4", OwnerId = owner3 });

        // Act
        var result = await _repository.GetByOwnerAsync(owner1);

        // Assert
        Assert.Equal(2, result.Count());
        Assert.All(result, p => Assert.Equal(owner1, p.OwnerId));
        Assert.Contains(result, p => p.Name == "Property 1");
        Assert.Contains(result, p => p.Name == "Property 3");
    }
}