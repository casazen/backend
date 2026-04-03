using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

/// <summary>
/// Tests for OTA booking deduplication logic in BookingRepository
/// </summary>
public class BookingRepositoryOtaTests
{
    private AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private async Task<(Guid propertyId, Guid guestId)> SeedTestDataAsync(AppDbContext context)
    {
        var property = new Property
        {
            OwnerId = "test-owner",
            Name = "Test Property",
            Address = "Test Address",
            City = "Test City"
        };
        var guest = new Guest
        {
            FirstName = "John",
            LastName = "Doe",
            Email = "john@example.com"
        };
        context.Properties.Add(property);
        context.Guests.Add(guest);
        await context.SaveChangesAsync();
        return (property.Id, guest.Id);
    }

    [Fact]
    public async Task GetByExternalIdAsync_WithExistingBooking_ReturnsBooking()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

        var booking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        // Act
        var repository = new BookingRepository(context);
        var result = await repository.GetByExternalIdAsync(propertyId, externalId, BookingSource.Airbnb);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(externalId, result.ExternalId);
        Assert.Equal(BookingSource.Airbnb, result.Source);
        Assert.Equal(propertyId, result.PropertyId);
    }

    [Fact]
    public async Task GetByExternalIdAsync_WithDifferentProperty_ReturnsNull()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var differentPropertyId = Guid.NewGuid();
        var externalId = "AIRBNB-12345";

        var booking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        // Act
        var repository = new BookingRepository(context);
        var result = await repository.GetByExternalIdAsync(differentPropertyId, externalId, BookingSource.Airbnb);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByExternalIdAsync_WithDifferentSource_ReturnsNull()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "12345";

        var booking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };
        context.Bookings.Add(booking);
        await context.SaveChangesAsync();

        // Act - Search for same external ID but different source
        var repository = new BookingRepository(context);
        var result = await repository.GetByExternalIdAsync(propertyId, externalId, BookingSource.BookingCom);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_WithNewBooking_CreatesBooking()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

        var booking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };

        // Act
        var repository = new BookingRepository(context);
        var result = await repository.UpsertOtaBookingAsync(booking);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(300, result.TotalPrice);
        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal(externalId, result.ExternalId);

        // Verify it was created in database
        var created = await context.Bookings.FindAsync(result.Id);
        Assert.NotNull(created);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_WithExistingBooking_UpdatesBooking()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

        // Create initial booking
        var initialBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Pending,
            TotalPrice = 300
        };
        context.Bookings.Add(initialBooking);
        await context.SaveChangesAsync();
        var existingId = initialBooking.Id;

        // Act - Update the booking
        var repository = new BookingRepository(context);
        var updatedBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed, // Changed status
            TotalPrice = 350 // Changed price
        };

        var result = await repository.UpsertOtaBookingAsync(updatedBooking);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(existingId, result.Id); // Same ID means it updated, not created
        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal(350, result.TotalPrice);

        // Verify only one booking exists
        var count = await context.Bookings.CountAsync(b => b.ExternalId == externalId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_WithStatusChange_UpdatesStatus()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

        // Create confirmed booking
        var initialBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };
        context.Bookings.Add(initialBooking);
        await context.SaveChangesAsync();

        // Act - Cancel the booking
        var repository = new BookingRepository(context);
        var cancelledBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Cancelled, // Changed to cancelled
            TotalPrice = 300
        };

        var result = await repository.UpsertOtaBookingAsync(cancelledBooking);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(BookingStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_DifferentSources_CreatesMultipleBookings()
    {
        // Arrange - Same external ID but different sources
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "12345"; // Same ID on different platforms

        var airbnbBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };

        var bookingComBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.BookingCom,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };

        // Act
        var repository = new BookingRepository(context);
        await repository.UpsertOtaBookingAsync(airbnbBooking);
        await repository.UpsertOtaBookingAsync(bookingComBooking);

        // Assert - Both bookings should exist
        var count = await context.Bookings.CountAsync(b => b.ExternalId == externalId);
        Assert.Equal(2, count); // Different sources, so both created
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_MultipleCalls_NoRaceCondition()
    {
        // Arrange
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

        var booking1 = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 300
        };

        var booking2 = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 310
        };

        // Act - Simulate sync pulling same booking twice
        var repository = new BookingRepository(context);
        await repository.UpsertOtaBookingAsync(booking1);
        await repository.UpsertOtaBookingAsync(booking2);

        // Assert - Only one booking should exist
        var count = await context.Bookings.CountAsync(b =>
            b.ExternalId == externalId &&
            b.Source == BookingSource.Airbnb);
        Assert.Equal(1, count);

        // And it should have the latest data
        var final = await repository.GetByExternalIdAsync(propertyId, externalId, BookingSource.Airbnb);
        Assert.NotNull(final);
        Assert.Equal(310, final.TotalPrice);
    }
}
