using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

public class PropertyUniquenessTests : IDisposable
{
    private readonly AppDbContext _context;

    public PropertyUniquenessTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
    }

    [Fact]
    public async Task CreateProperty_WithSameAddressButDifferentCity_ShouldSucceed()
    {
        var property1 = new Property
        {
            Name = "Property 1",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            OwnerId = "owner123",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100,
            IsActive = true
        };

        var property2 = new Property
        {
            Name = "Property 2",
            Address = "Via Roma 1",
            City = "Milan",
            PostalCode = "20100",
            OwnerId = "owner123",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100,
            IsActive = true
        };

        _context.Properties.Add(property1);
        _context.Properties.Add(property2);
        await _context.SaveChangesAsync();

        var properties = await _context.Properties.ToListAsync();
        Assert.Equal(2, properties.Count);
    }

    [Fact]
    public async Task CreateProperty_AtSameAddressAfterSoftDelete_ShouldSucceed()
    {
        var property1 = new Property
        {
            Name = "Property 1",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            OwnerId = "owner123",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100,
            IsActive = true
        };

        _context.Properties.Add(property1);
        await _context.SaveChangesAsync();

        property1.IsActive = false;
        await _context.SaveChangesAsync();

        var property2 = new Property
        {
            Name = "Property 2",
            Address = "Via Roma 1",
            City = "Rome",
            PostalCode = "00100",
            OwnerId = "owner456",
            Bedrooms = 3,
            Bathrooms = 2,
            MaxGuests = 6,
            NightlyRate = 150,
            IsActive = true
        };

        _context.Properties.Add(property2);
        await _context.SaveChangesAsync();

        var properties = await _context.Properties.ToListAsync();
        Assert.Equal(2, properties.Count);
        Assert.Single(properties, p => p.IsActive);
    }

    public void Dispose()
    {
        _context?.Dispose();
    }
}
