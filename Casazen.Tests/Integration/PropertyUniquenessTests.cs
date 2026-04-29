using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

public class PropertyUniquenessTests : IAsyncLifetime
{
    private readonly DbContextOptions<AppDbContext> _options;
    private AppDbContext _context = null!;

    public PropertyUniquenessTests()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"test_db_{Guid.NewGuid()}")
            .Options;
    }

    public async Task InitializeAsync()
    {
        _context = new AppDbContext(_options);
        await _context.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
    }

    [Fact]
    public async Task CreateProperty_WithUniqueAddress_Succeeds()
    {
        // Arrange
        var property = new Property
        {
            OwnerId = "owner_123",
            Name = "Beautiful Villa",
            Description = "A wonderful place to stay",
            Address = "Via Roma 123",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 3,
            Bathrooms = 2,
            MaxGuests = 6,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 500m
        };

        // Act
        _context.Properties.Add(property);
        await _context.SaveChangesAsync();

        // Assert
        var savedProperty = await _context.Properties.FirstOrDefaultAsync(p => p.Address == "Via Roma 123");
        Assert.NotNull(savedProperty);
        Assert.Equal("Rome", savedProperty.City);
        Assert.Equal("00100", savedProperty.PostalCode);
    }

    [Fact]
    public async Task CreateProperty_WithDifferentCities_Succeeds()
    {
        // Arrange
        var property1 = new Property
        {
            OwnerId = "owner_123",
            Name = "Rome Property",
            Description = "Property in Rome",
            Address = "Via Roma 123",
            City = "Rome",
            PostalCode = "00100",
            Bedrooms = 3,
            Bathrooms = 2,
            MaxGuests = 6,
            NightlyRate = 150m,
            CleaningFee = 50m,
            DamageDeposit = 500m
        };

        var property2 = new Property
        {
            OwnerId = "owner_456",
            Name = "Milan Property",
            Description = "Property in Milan",
            Address = "Via Roma 123",  // Same address name
            City = "Milan",            // Different city
            PostalCode = "20100",      // Different postal code
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CleaningFee = 40m,
            DamageDeposit = 400m
        };

        // Act
        _context.Properties.Add(property1);
        await _context.SaveChangesAsync();

        _context.Properties.Add(property2);
        await _context.SaveChangesAsync();

        // Assert
        var properties = await _context.Properties.ToListAsync();
        Assert.Equal(2, properties.Count);
        Assert.Single(properties, p => p.City == "Rome");
        Assert.Single(properties, p => p.City == "Milan");
    }
}
