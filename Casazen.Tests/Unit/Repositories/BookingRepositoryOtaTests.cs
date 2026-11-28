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

        var repository = new BookingRepository(context);
        var result = await repository.GetByExternalIdAsync(propertyId, externalId, BookingSource.Airbnb);

        Assert.NotNull(result);
        Assert.Equal(externalId, result.ExternalId);
        Assert.Equal(BookingSource.Airbnb, result.Source);
        Assert.Equal(propertyId, result.PropertyId);
    }

    [Fact]
    public async Task GetByExternalIdAsync_WithDifferentProperty_ReturnsNull()
    {
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

        var repository = new BookingRepository(context);
        var result = await repository.GetByExternalIdAsync(differentPropertyId, externalId, BookingSource.Airbnb);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetByExternalIdAsync_WithDifferentSource_ReturnsNull()
    {
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

        var repository = new BookingRepository(context);
        var result = await repository.GetByExternalIdAsync(propertyId, externalId, BookingSource.BookingCom);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_WithNewBooking_CreatesBooking()
    {
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

        var repository = new BookingRepository(context);
        var result = await repository.UpsertOtaBookingAsync(booking);

        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.Id);
        Assert.Equal(300, result.TotalPrice);
        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal(externalId, result.ExternalId);

        var created = await context.Bookings.FindAsync(result.Id);
        Assert.NotNull(created);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_WithExistingBooking_UpdatesBooking()
    {
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

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

        var repository = new BookingRepository(context);
        var updatedBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Confirmed,
            TotalPrice = 350
        };

        var result = await repository.UpsertOtaBookingAsync(updatedBooking);

        Assert.NotNull(result);
        Assert.Equal(existingId, result.Id);
        Assert.Equal(BookingStatus.Confirmed, result.Status);
        Assert.Equal(350, result.TotalPrice);

        var count = await context.Bookings.CountAsync(b => b.ExternalId == externalId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_WithStatusChange_UpdatesStatus()
    {
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "AIRBNB-12345";

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

        var repository = new BookingRepository(context);
        var cancelledBooking = new Booking
        {
            PropertyId = propertyId,
            GuestId = guestId,
            ExternalId = externalId,
            Source = BookingSource.Airbnb,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
            Status = BookingStatus.Cancelled,
            TotalPrice = 300
        };

        var result = await repository.UpsertOtaBookingAsync(cancelledBooking);

        Assert.NotNull(result);
        Assert.Equal(BookingStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_DifferentSources_CreatesMultipleBookings()
    {
        using var context = CreateContext();
        var (propertyId, guestId) = await SeedTestDataAsync(context);
        var externalId = "12345";

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

        var repository = new BookingRepository(context);
        await repository.UpsertOtaBookingAsync(airbnbBooking);
        await repository.UpsertOtaBookingAsync(bookingComBooking);

        var count = await context.Bookings.CountAsync(b => b.ExternalId == externalId);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task UpsertOtaBookingAsync_MultipleCalls_NoRaceCondition()
    {
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

        var repository = new BookingRepository(context);
        await repository.UpsertOtaBookingAsync(booking1);
        await repository.UpsertOtaBookingAsync(booking2);

        var count = await context.Bookings.CountAsync(b =>
            b.ExternalId == externalId &&
            b.Source == BookingSource.Airbnb);
        Assert.Equal(1, count);

        var final = await repository.GetByExternalIdAsync(propertyId, externalId, BookingSource.Airbnb);
        Assert.NotNull(final);
        Assert.Equal(310, final.TotalPrice);
    }
}
