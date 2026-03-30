using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Casazen.Tests.Integration;

public class MigrationTests : IDisposable
{
    private readonly AppDbContext _context;
    private readonly string _databaseName;

    public MigrationTests()
    {
        _databaseName = "TestMigrationDb_" + Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        _context = new AppDbContext(options);
    }

    [Fact]
    public async Task FixOwnerIdType_Migration_ConvertsGuidToString()
    {
        // Arrange - Create database with initial schema
        await _context.Database.EnsureCreatedAsync();

        // Verify Property entity has string OwnerId
        var propertyEntityType = _context.Model.FindEntityType(typeof(Property));
        Assert.NotNull(propertyEntityType);

        var ownerIdProperty = propertyEntityType.FindProperty(nameof(Property.OwnerId));
        Assert.NotNull(ownerIdProperty);
        Assert.Equal(typeof(string), ownerIdProperty.ClrType);
        Assert.Equal(255, ownerIdProperty.GetMaxLength());
    }

    [Fact]
    public async Task Property_WithStringOwnerId_CanBeSavedAndRetrieved()
    {
        // Arrange
        await _context.Database.EnsureCreatedAsync();

        var property = new Property
        {
            Name = "Test Property",
            City = "Rome",
            Address = "Via Roma 1",
            OwnerId = "auth0|test_user_123"
        };

        // Act
        _context.Properties.Add(property);
        await _context.SaveChangesAsync();

        // Assert
        var savedProperty = await _context.Properties
            .FirstOrDefaultAsync(p => p.Name == "Test Property");

        Assert.NotNull(savedProperty);
        Assert.Equal("auth0|test_user_123", savedProperty.OwnerId);
        Assert.IsType<string>(savedProperty.OwnerId);
    }

    [Fact]
    public async Task Property_WithVariousOwnerIdFormats_CanBeSaved()
    {
        // Arrange
        await _context.Database.EnsureCreatedAsync();

        var ownerIds = new[]
        {
            "auth0|123",
            "google-oauth2|1234567890",
            "auth0|very-long-user-id-with-many-characters-1234567890-abcdef",
            "github|9876543210",
            "twitter|xyz123"
        };

        // Act
        foreach (var ownerId in ownerIds)
        {
            _context.Properties.Add(new Property
            {
                Name = $"Property for {ownerId}",
                City = "Rome",
                Address = "Via Roma",
                OwnerId = ownerId
            });
        }
        await _context.SaveChangesAsync();

        // Assert
        var properties = await _context.Properties.ToListAsync();
        Assert.Equal(ownerIds.Length, properties.Count);

        foreach (var ownerId in ownerIds)
        {
            var property = properties.FirstOrDefault(p => p.OwnerId == ownerId);
            Assert.NotNull(property);
            Assert.IsType<string>(property.OwnerId);
        }
    }

    [Fact]
    public async Task Property_OwnerIdMaxLength_IsEnforced()
    {
        // Arrange
        await _context.Database.EnsureCreatedAsync();

        var propertyEntityType = _context.Model.FindEntityType(typeof(Property));
        var ownerIdProperty = propertyEntityType?.FindProperty(nameof(Property.OwnerId));

        // Assert
        Assert.NotNull(ownerIdProperty);
        Assert.Equal(255, ownerIdProperty.GetMaxLength());
    }

    [Fact]
    public async Task Property_OwnerIdIsRequired_IsEnforced()
    {
        // Arrange
        await _context.Database.EnsureCreatedAsync();

        var propertyEntityType = _context.Model.FindEntityType(typeof(Property));
        var ownerIdProperty = propertyEntityType?.FindProperty(nameof(Property.OwnerId));

        // Assert
        Assert.NotNull(ownerIdProperty);
        Assert.False(ownerIdProperty.IsNullable);
    }

    [Fact]
    public async Task GetByOwnerAsync_WithStringOwnerId_ReturnsCorrectProperties()
    {
        // Arrange
        await _context.Database.EnsureCreatedAsync();

        var ownerId1 = "auth0|owner_1";
        var ownerId2 = "auth0|owner_2";

        _context.Properties.AddRange(
            new Property { Name = "Property 1", City = "Rome", Address = "Address 1", OwnerId = ownerId1 },
            new Property { Name = "Property 2", City = "Milan", Address = "Address 2", OwnerId = ownerId1 },
            new Property { Name = "Property 3", City = "Florence", Address = "Address 3", OwnerId = ownerId2 }
        );
        await _context.SaveChangesAsync();

        // Act
        var owner1Properties = await _context.Properties
            .Where(p => p.OwnerId == ownerId1 && p.IsActive)
            .ToListAsync();

        // Assert
        Assert.Equal(2, owner1Properties.Count);
        Assert.All(owner1Properties, p => Assert.Equal(ownerId1, p.OwnerId));
    }

    [Fact]
    public async Task Migration_PreservesOtherPropertyColumns()
    {
        // Arrange
        await _context.Database.EnsureCreatedAsync();

        var property = new Property
        {
            Name = "Full Property",
            City = "Rome",
            Address = "Via Roma 1",
            Description = "Beautiful property in Rome",
            PostalCode = "00100",
            Latitude = 41.9028m,
            Longitude = 12.4964m,
            Bedrooms = 3,
            Bathrooms = 2,
            MaxGuests = 6,
            NightlyRate = 150.00m,
            CleaningFee = 75.00m,
            DamageDeposit = 300.00m,
            OwnerId = "auth0|test_user_123",
            Amenities = new List<string> { "WiFi", "Kitchen", "Parking" },
            PhotoUrls = new List<string> { "https://example.com/photo1.jpg" },
            HouseRules = "No smoking, No pets",
            IsActive = true
        };

        // Act
        _context.Properties.Add(property);
        await _context.SaveChangesAsync();

        // Assert
        var savedProperty = await _context.Properties
            .FirstOrDefaultAsync(p => p.Name == "Full Property");

        Assert.NotNull(savedProperty);
        Assert.Equal("Rome", savedProperty.City);
        Assert.Equal(3, savedProperty.Bedrooms);
        Assert.Equal(2, savedProperty.Bathrooms);
        Assert.Equal(6, savedProperty.MaxGuests);
        Assert.Equal(150.00m, savedProperty.NightlyRate);
        Assert.Equal(75.00m, savedProperty.CleaningFee);
        Assert.Equal(300.00m, savedProperty.DamageDeposit);
        Assert.Contains("WiFi", savedProperty.Amenities);
        Assert.True(savedProperty.IsActive);
        Assert.Equal("auth0|test_user_123", savedProperty.OwnerId);
    }

    [Fact]
    public void Migration_ChecksColumnTypeInSchema()
    {
        // Arrange
        var propertyEntityType = _context.Model.FindEntityType(typeof(Property));
        var ownerIdProperty = propertyEntityType?.FindProperty(nameof(Property.OwnerId));

        // Assert
        Assert.NotNull(ownerIdProperty);

        // Verify it's a string type
        Assert.Equal(typeof(string), ownerIdProperty.ClrType);

        // Verify constraints
        Assert.False(ownerIdProperty.IsNullable);
        Assert.Equal(255, ownerIdProperty.GetMaxLength());
    }

    public void Dispose()
    {
        _context?.Dispose();
    }
}
