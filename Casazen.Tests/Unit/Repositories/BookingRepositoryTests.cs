using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Repositories;

public class BookingRepositoryTests
{
    private readonly AppDbContext _context;
    private readonly BookingRepository _repository;
    private readonly Guid _propertyId = Guid.NewGuid();

    public BookingRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new AppDbContext(options);
        _repository = new BookingRepository(_context);
    }

    private async Task SeedBookingAsync(DateTime checkIn, DateTime checkOut, BookingStatus status = BookingStatus.Confirmed)
    {
        await _repository.AddAsync(new Booking
        {
            PropertyId = _propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = status,
            NumberOfGuests = 2,
            TotalPrice = 300m
        });
    }

    [Fact]
    public async Task IsAvailableAsync_NoExistingBookings_ReturnsTrue()
    {
        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 5));

        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_SameDayTurnover_ReturnsTrue()
    {
        // Existing: Apr 01-05; new checkin Apr 05 = checkout date of existing
        await SeedBookingAsync(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 5));

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 5),
            new DateTime(2026, 4, 10));

        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithOneDayOverlap_ReturnsFalse()
    {
        // Existing: Apr 01-05; new Apr 04-10 overlaps on Apr 04
        await SeedBookingAsync(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 5));

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 4),
            new DateTime(2026, 4, 10));

        Assert.False(result);
    }

    [Fact]
    public async Task AddAsync_WithOverlappingActiveBooking_Throws()
    {
        await SeedBookingAsync(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 5));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.AddAsync(new Booking
            {
                PropertyId = _propertyId,
                GuestId = Guid.NewGuid(),
                CheckInDate = new DateTime(2026, 4, 4),
                CheckOutDate = new DateTime(2026, 4, 10),
                Status = BookingStatus.Pending,
                NumberOfGuests = 2,
                TotalPrice = 300m
            }));

        Assert.Contains("Property not available", ex.Message);
    }

    [Fact]
    public async Task UpdateAsync_WithOverlappingActiveBooking_Throws()
    {
        await SeedBookingAsync(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 5));

        var booking = await _repository.AddAsync(new Booking
        {
            PropertyId = _propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 4, 5),
            CheckOutDate = new DateTime(2026, 4, 10),
            Status = BookingStatus.Pending,
            NumberOfGuests = 2,
            TotalPrice = 300m
        });

        booking.CheckInDate = new DateTime(2026, 4, 4);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.UpdateAsync(booking));

        Assert.Contains("Property not available", ex.Message);
    }

    [Fact]
    public async Task IsAvailableAsync_WithFullOverlap_ReturnsFalse()
    {
        // New booking is entirely inside existing booking
        await SeedBookingAsync(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 10));

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 3),
            new DateTime(2026, 4, 7));

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithEnclosingOverlap_ReturnsFalse()
    {
        // New booking encloses the existing booking
        await SeedBookingAsync(
            new DateTime(2026, 4, 3),
            new DateTime(2026, 4, 7));

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 10));

        Assert.False(result);
    }

    [Fact]
    public async Task IsAvailableAsync_WithTimeComponents_SameDayTurnover_ReturnsTrue()
    {
        // Existing checkout at 10:00, new checkin at 15:00 same day - valid turnover
        await SeedBookingAsync(
            new DateTime(2026, 4, 1, 14, 0, 0),
            new DateTime(2026, 4, 5, 10, 0, 0));

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 5, 15, 0, 0),
            new DateTime(2026, 4, 10, 14, 0, 0));

        Assert.True(result);
    }

    [Fact]
    public async Task IsAvailableAsync_CancelledBooking_DoesNotBlock()
    {
        await SeedBookingAsync(
            new DateTime(2026, 4, 1),
            new DateTime(2026, 4, 10),
            BookingStatus.Cancelled);

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 3),
            new DateTime(2026, 4, 7));

        Assert.True(result);
    }

    [Fact]
    public async Task AddAsync_ConfirmedBooking_SetsCheckInTokenAndExpiry()
    {
        var checkOut = new DateTime(2026, 5, 10);
        var booking = await _repository.AddAsync(new Booking
        {
            PropertyId = _propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 5, 1),
            CheckOutDate = checkOut,
            Status = BookingStatus.Confirmed,
            NumberOfGuests = 2,
            TotalPrice = 300m
        });

        Assert.NotNull(booking.CheckInToken);
        Assert.NotNull(booking.CheckInTokenExpiresAt);
        Assert.Equal(checkOut.AddDays(7), booking.CheckInTokenExpiresAt);
    }

    [Fact]
    public async Task IsAvailableAsync_DifferentProperty_ReturnsTrue()
    {
        // Conflict exists for a different property - must not affect target property
        var otherPropertyId = Guid.NewGuid();
        await _repository.AddAsync(new Booking
        {
            PropertyId = otherPropertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 4, 1),
            CheckOutDate = new DateTime(2026, 4, 10),
            Status = BookingStatus.Confirmed,
            NumberOfGuests = 2,
            TotalPrice = 300m
        });

        var result = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 3),
            new DateTime(2026, 4, 7));

        Assert.True(result);
    }
}
