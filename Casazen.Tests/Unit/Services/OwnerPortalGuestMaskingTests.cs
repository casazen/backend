using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// ADR-011-R3: Owner portal responses MUST mask guest data.
/// Proves that guest names, emails, and phones are NOT exposed to property owners.
/// </summary>
public class OwnerPortalGuestMaskingTests : IAsyncLifetime
{
    private AppDbContext _db = null!;
    private IOwnerPortalService _service = null!;
    private Guid _ownerId;
    private Guid _propertyId;
    private Guid _bookingId;
    private const string SensitiveGuestName = "Mario Rossi";
    private const string SensitiveGuestEmail = "mario.rossi@example.com";
    private const string SensitiveGuestPhone = "+39 123 456 7890";

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: $"OwnerPortalGuestMasking_{Guid.NewGuid()}")
            .Options;
        _db = new AppDbContext(options);

        var timeProvider = TimeProvider.System;
        _service = new OwnerPortalService(_db, timeProvider);

        // Arrange: Create test data with sensitive guest information
        var org = new OrgEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Agency",
            Slug = "test-agency",
            PlanTier = PlanTier.Agency,
            CreatedAt = DateTime.UtcNow
        };

        var ownerUser = new OwnerUser
        {
            Id = Guid.NewGuid(),
            Auth0Sub = "auth0|owner123",
            Email = "owner@example.com",
            FirstName = "Owner",
            LastName = "Test",
            CreatedAt = DateTime.UtcNow
        };
        _ownerId = ownerUser.Id;

        var property = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            Name = "Test Property",
            Address = "123 Test Street",
            MaxGuests = 4,
            CreatedAt = DateTime.UtcNow
        };
        _propertyId = property.Id;

        var propertyOwner = new PropertyOwner
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            FirstName = "Registry",
            LastName = "Owner",
            Email = "registry@example.com",
            CreatedAt = DateTime.UtcNow
        };

        var ownership = new PropertyOwnership
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            PropertyId = property.Id,
            PropertyOwnerId = propertyOwner.Id,
            OwnerUserId = ownerUser.Id,
            SharePercent = 100.00m,
            OwnershipStart = DateTime.UtcNow.AddMonths(-6),
            OwnershipEnd = null // Active ownership
        };

        var guest = new Guest
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = SensitiveGuestEmail,
            PhoneNumber = SensitiveGuestPhone,
            CreatedAt = DateTime.UtcNow
        };

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            OrgId = org.Id,
            PropertyId = property.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.AddDays(10),
            CheckOutDate = DateTime.UtcNow.AddDays(15),
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Manual,
            TotalPrice = 500.00m,
            CreatedAt = DateTime.UtcNow
        };
        _bookingId = booking.Id;

        await _db.Set<OrgEntity>().AddAsync(org);
        await _db.Set<OwnerUser>().AddAsync(ownerUser);
        await _db.Set<Property>().AddAsync(property);
        await _db.Set<PropertyOwner>().AddAsync(propertyOwner);
        await _db.Set<PropertyOwnership>().AddAsync(ownership);
        await _db.Set<Guest>().AddAsync(guest);
        await _db.Set<Booking>().AddAsync(booking);
        await _db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Proof test: OwnerPortal_GuestData_Masked (ADR-011-R3).
    /// Verifies that guest names, emails, and phones are NOT in the owner portal response.
    /// Only dates, channel, and amounts are visible.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_GuestData_Masked()
    {
        // Act: Get properties as the owner would through the portal
        var properties = await _service.GetOwnerPropertiesAsync(_ownerId);

        // Assert: Property data is present
        Assert.NotEmpty(properties);
        var property = properties.First();
        Assert.Equal(_propertyId, property.PropertyId);
        Assert.Equal("Test Property", property.PropertyName);

        // Assert: Booking summaries are present
        Assert.NotEmpty(property.RecentBookings);
        var bookingSummary = property.RecentBookings.First();
        Assert.Equal(_bookingId, bookingSummary.BookingId);

        // Assert: Dates, channel, and amounts ARE visible (ADR-011-R3)
        Assert.NotEqual(default(DateTime), bookingSummary.CheckInDate);
        Assert.NotEqual(default(DateTime), bookingSummary.CheckOutDate);
        Assert.NotEmpty(bookingSummary.Channel);
        Assert.Equal(500.00m, bookingSummary.TotalAmount);

        // Assert: Guest names are NOT in the response (ADR-011-R3)
        // The service returns only booking data, guest names/emails/phones are never included
        // This is verified by the fact that OwnerPortalBookingInfo does not have guest fields
        // and the service query does not join or select guest data

        // Additional proof: Serialize the entire response and verify no guest PII appears
        var responseJson = System.Text.Json.JsonSerializer.Serialize(properties);

        // MUST NOT contain sensitive guest data (ADR-011-R3)
        Assert.DoesNotContain(SensitiveGuestName, responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SensitiveGuestEmail, responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SensitiveGuestPhone, responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Mario", responseJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rossi", responseJson, StringComparison.OrdinalIgnoreCase);

        // Verify only safe data is present
        Assert.Contains("Test Property", responseJson);
        Assert.Contains("Manual", responseJson); // Channel
        Assert.Contains("500", responseJson); // Amount
    }

    [Fact]
    public async Task OwnerPortal_OnlyActiveOwnership_Visible()
    {
        // Arrange: Create another property with ended ownership
        var expiredProperty = new Property
        {
            Id = Guid.NewGuid(),
            OrgId = _db.Set<Property>().First().OrgId,
            Name = "Expired Property",
            Address = "456 Old Street",
            MaxGuests = 2,
            CreatedAt = DateTime.UtcNow
        };

        var expiredOwnership = new PropertyOwnership
        {
            Id = Guid.NewGuid(),
            OrgId = expiredProperty.OrgId,
            PropertyId = expiredProperty.Id,
            PropertyOwnerId = _db.Set<PropertyOwner>().First().Id,
            OwnerUserId = _ownerId,
            SharePercent = 100.00m,
            OwnershipStart = DateTime.UtcNow.AddYears(-2),
            OwnershipEnd = DateTime.UtcNow.AddMonths(-1) // Ended last month
        };

        await _db.Set<Property>().AddAsync(expiredProperty);
        await _db.Set<PropertyOwnership>().AddAsync(expiredOwnership);
        await _db.SaveChangesAsync();

        // Act: Get properties
        var properties = await _service.GetOwnerPropertiesAsync(_ownerId);

        // Assert: Only active ownership is visible (ADR-011-R2, ADR-011-R8)
        Assert.Single(properties);
        Assert.Equal(_propertyId, properties.First().PropertyId);
        Assert.DoesNotContain(properties, p => p.PropertyId == expiredProperty.Id);
    }
}
