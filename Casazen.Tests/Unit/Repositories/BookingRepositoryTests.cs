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
    public async Task IsAvailableAsync_ExpiredPendingDirectBookingWithTtl_DoesNotBlock()
    {
        await _repository.AddAsync(new Booking
        {
            PropertyId = _propertyId,
            GuestId = Guid.NewGuid(),
            CheckInDate = new DateTime(2026, 4, 1),
            CheckOutDate = new DateTime(2026, 4, 10),
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            StripeSetupIntentId = "seti_expired",
            CreatedAt = DateTime.UtcNow.AddMinutes(-20),
            NumberOfGuests = 2,
            TotalPrice = 300m
        });

        var strictResult = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 3),
            new DateTime(2026, 4, 7));
        var ttlResult = await _repository.IsAvailableAsync(
            _propertyId,
            new DateTime(2026, 4, 3),
            new DateTime(2026, 4, 7),
            directPendingTtlMinutes: 15);

        Assert.False(strictResult);
        Assert.True(ttlResult);
    }

    [Fact]
    public async Task GetByDateRangeAsync_WithTtl_LeavesOutOnlyExpiredCheckoutHolds()
    {
        // BK-21: one predicate (CheckoutHolds) decides which Pending bookings no longer take their dates.
        var expiredSetupHold = await AddBookingAsync(new DateTime(2026, 4, 1), new DateTime(2026, 4, 3), minutesAgo: 20,
            b => b.StripeSetupIntentId = "seti_expired");
        var expiredPaymentHold = await AddBookingAsync(new DateTime(2026, 4, 3), new DateTime(2026, 4, 5), minutesAgo: 20);
        AddPayment(expiredPaymentHold, "pi_expired", PaymentStatus.Pending);
        var paymentInFlightHold = await AddBookingAsync(new DateTime(2026, 4, 5), new DateTime(2026, 4, 7), minutesAgo: 20);
        AddPayment(paymentInFlightHold, "pi_processing", PaymentStatus.Processing);
        var freshHold = await AddBookingAsync(new DateTime(2026, 4, 7), new DateTime(2026, 4, 9), minutesAgo: 5,
            b => b.StripeSetupIntentId = "seti_fresh");
        var pendingWithoutIntent = await AddBookingAsync(new DateTime(2026, 4, 9), new DateTime(2026, 4, 11), minutesAgo: 20);
        var onSiteRequest = await AddBookingAsync(new DateTime(2026, 4, 11), new DateTime(2026, 4, 13), minutesAgo: 20, b =>
        {
            b.PaymentOption = PaymentOption.OnSite;
            b.StripeSetupIntentId = "seti_onsite_guarantee";
            b.RequestExpiresAt = DateTime.UtcNow.AddHours(1);
        });
        var manual = await AddBookingAsync(new DateTime(2026, 4, 13), new DateTime(2026, 4, 15), minutesAgo: 20, b =>
        {
            b.Status = BookingStatus.Confirmed;
            b.Source = BookingSource.Manual;
        });
        await _context.SaveChangesAsync();

        var withTtl = (await _repository.GetByDateRangeAsync(
            _propertyId, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30), directPendingTtlMinutes: 15))
            .Select(b => b.Id).ToHashSet();
        var strict = (await _repository.GetByDateRangeAsync(
            _propertyId, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30)))
            .Select(b => b.Id).ToHashSet();

        Assert.DoesNotContain(expiredSetupHold.Id, withTtl);
        Assert.DoesNotContain(expiredPaymentHold.Id, withTtl);
        Assert.Contains(paymentInFlightHold.Id, withTtl);
        Assert.Contains(freshHold.Id, withTtl);
        Assert.Contains(pendingWithoutIntent.Id, withTtl);
        Assert.Contains(onSiteRequest.Id, withTtl);
        Assert.Contains(manual.Id, withTtl);
        Assert.Equal(7, strict.Count);
    }

    [Fact]
    public async Task IsAvailableAsync_OnSiteRequest_BlocksUntilItsOwnDeadlineWhateverTheCheckoutTtl()
    {
        // BK-06: a "pay at the property" request holds its dates until RequestExpiresAt (email confirmation, then the
        // host's answer), not for the checkout TTL; past it the dates are free before the job cancels it.
        await AddBookingAsync(new DateTime(2026, 6, 1), new DateTime(2026, 6, 5), minutesAgo: 600, b =>
        {
            b.PaymentOption = PaymentOption.OnSite;
            b.GuestEmailVerifiedAt = DateTime.UtcNow.AddMinutes(-590);
            b.RequestExpiresAt = DateTime.UtcNow.AddHours(2);
        });
        await AddBookingAsync(new DateTime(2026, 6, 10), new DateTime(2026, 6, 12), minutesAgo: 5, b =>
        {
            b.PaymentOption = PaymentOption.OnSite;
            b.RequestExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        });

        Assert.False(await _repository.IsAvailableAsync(
            _propertyId, new DateTime(2026, 6, 2), new DateTime(2026, 6, 4), directPendingTtlMinutes: 15));
        Assert.True(await _repository.IsAvailableAsync(
            _propertyId, new DateTime(2026, 6, 10), new DateTime(2026, 6, 12), directPendingTtlMinutes: 15));
        // The final check before an insert stays strict until the job has cancelled it.
        Assert.False(await _repository.IsAvailableAsync(
            _propertyId, new DateTime(2026, 6, 10), new DateTime(2026, 6, 12)));
    }

    [Fact]
    public async Task IsAvailableAsync_ExpiredHoldWhosePaymentIsProcessing_BlocksWithTtl()
    {
        // Stripe said the guest is paying (recorded by the expiry as Processing): the dates stay taken.
        var hold = await AddBookingAsync(new DateTime(2026, 5, 1), new DateTime(2026, 5, 5), minutesAgo: 30);
        AddPayment(hold, "pi_processing", PaymentStatus.Processing);
        await _context.SaveChangesAsync();

        Assert.False(await _repository.IsAvailableAsync(
            _propertyId, new DateTime(2026, 5, 2), new DateTime(2026, 5, 4), directPendingTtlMinutes: 15));
    }

    [Fact]
    public async Task IsAvailableAsync_HostPendingDirectBookingWithoutIntent_BlocksWithTtl()
    {
        await AddBookingAsync(new DateTime(2026, 4, 1), new DateTime(2026, 4, 10), minutesAgo: 30);

        Assert.False(await _repository.IsAvailableAsync(
            _propertyId, new DateTime(2026, 4, 2), new DateTime(2026, 4, 9), directPendingTtlMinutes: 15));
    }

    [Fact]
    public async Task IsAvailableAsync_ManualConfirmedBooking_BlocksWithTtl()
    {
        await AddBookingAsync(new DateTime(2026, 10, 1), new DateTime(2026, 10, 5), minutesAgo: 30, b =>
        {
            b.Status = BookingStatus.Confirmed;
            b.Source = BookingSource.Manual;
        });

        Assert.False(await _repository.IsAvailableAsync(
            _propertyId, new DateTime(2026, 10, 2), new DateTime(2026, 10, 4), directPendingTtlMinutes: 15));
    }

    private async Task<Booking> AddBookingAsync(
        DateTime checkIn,
        DateTime checkOut,
        int minutesAgo,
        Action<Booking>? configure = null)
    {
        var orgId = Guid.NewGuid();
        var guest = new Guest { OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = "anna@example.com" };
        var booking = new Booking
        {
            PropertyId = _propertyId,
            OrgId = orgId,
            GuestId = guest.Id,
            Guest = guest,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo),
            NumberOfGuests = 2,
            TotalPrice = 300m,
        };
        configure?.Invoke(booking);
        return await _repository.AddAsync(booking);
    }

    private void AddPayment(Booking booking, string paymentIntentId, PaymentStatus status) =>
        _context.Payments.Add(new Payment
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            Amount = booking.TotalPrice,
            Status = status,
            StripePaymentIntentId = paymentIntentId,
            TransactionId = paymentIntentId,
        });

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
