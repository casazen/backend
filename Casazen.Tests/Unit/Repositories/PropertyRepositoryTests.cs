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
            OwnerId = Guid.NewGuid()
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
            OwnerId = Guid.NewGuid()
        });

        await _repository.AddAsync(new Property
        {
            Name = "Milan Property",
            City = "Milan",
            Address = "Via Milano",
            OwnerId = Guid.NewGuid()
        });

        // Act
        var result = await _repository.SearchAsync("Rome", null, null);

        // Assert
        Assert.Single(result);
        Assert.Equal("Rome Property", result.First().Name);
    }
}