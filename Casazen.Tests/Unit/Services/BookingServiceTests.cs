using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class BookingServiceTests
{
    private readonly Mock<IBookingRepository> _mockRepository;
    private readonly BookingService _service;

    public BookingServiceTests()
    {
        _mockRepository = new Mock<IBookingRepository>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DirectBooking:ConsentVersion"] = "2026-06-direct-checkout-v1",
                ["DirectBooking:PendingTtlMinutes"] = "15",
                ["Stripe:PublishableKey"] = "pk_test",
            })
            .Build();

        _service = new BookingService(
            _mockRepository.Object,
            new Mock<IPropertyRepository>().Object,
            new Mock<IOrgService>().Object,
            new Mock<IGuestRepository>().Object,
            new Mock<ITaxCalculationService>().Object,
            new Mock<IStripeService>().Object,
            new Mock<IPaymentRepository>().Object,
            CreatePropertyICalSyncService(configuration),
            configuration,
            new Mock<ILogger<BookingService>>().Object);
    }

    private static PropertyICalSyncService CreatePropertyICalSyncService(IConfiguration configuration)
    {
        var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        return new PropertyICalSyncService(
            db,
            Mock.Of<ISafeExternalHttpClient>(),
            new ICalImportService(),
            new ICalExportService(),
            configuration,
            Mock.Of<ILogger<PropertyICalSyncService>>());
    }

    [Fact]
    public async Task CreateDirectBookingAsync_WithInvalidPaymentOption_RejectsBeforePersisting()
    {
        var input = new DirectBookingCreateInput(
            Guid.NewGuid(),
            DateTime.UtcNow.Date.AddDays(10),
            DateTime.UtcNow.Date.AddDays(12),
            1,
            0,
            new DirectBookingGuestInput(
                "Ada",
                "Lovelace",
                "ada@example.com",
                null,
                "IT"),
            "2026-06-direct-checkout-v1",
            "127.0.0.1",
            null,
            (PaymentOption)999);

        var ex = await Assert.ThrowsAsync<DirectBookingException>(() =>
            _service.CreateDirectBookingAsync(input));

        Assert.Equal(DirectBookingErrorCodes.InvalidPaymentOption, ex.ErrorCode);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
        _mockRepository.Verify(x => x.CancelExpiredPendingDirectBookingsAsync(
            It.IsAny<Guid>(),
            It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateBookingAsync_WithValidBooking_ReturnsCreatedBooking()
    {
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(5),
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync(booking);
        _mockRepository.Setup(x => x.IsAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<int?>()))
            .ReturnsAsync(true);

        var result = await _service.CreateBookingAsync(booking);

        Assert.NotNull(result);
        Assert.Equal(booking.PropertyId, result.PropertyId);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Once);
        _mockRepository.Verify(x => x.CancelExpiredPendingDirectBookingsAsync(booking.PropertyId, 15), Times.Once);
        _mockRepository.Verify(x => x.IsAvailableAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, 15), Times.Once);
    }

    [Fact]
    public async Task CreateBookingAsync_WithPastCheckIn_ThrowsValidationError()
    {
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow.Date.AddDays(-1),
            CheckOutDate = DateTime.UtcNow.Date.AddDays(1),
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.CreateBookingAsync(booking));

        Assert.Contains("Check-in date cannot be in the past", ex.Message);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task IsPropertyAvailableAsync_WithAvailableProperty_ReturnsTrue()
    {
        var propertyId = Guid.NewGuid();
        var checkIn = DateTime.Now.AddDays(10);
        var checkOut = DateTime.Now.AddDays(15);
        _mockRepository.Setup(x => x.IsAvailableAsync(propertyId, checkIn, checkOut, 15)).ReturnsAsync(true);

        var result = await _service.IsPropertyAvailableAsync(propertyId, checkIn, checkOut);

        Assert.True(result);
        _mockRepository.Verify(x => x.CancelExpiredPendingDirectBookingsAsync(propertyId, 15), Times.Once);
    }

    [Fact]
    public async Task GetCalendarAsync_CancelsExpiredPendingDirectBookingsBeforeLoadingCalendar()
    {
        var propertyId = Guid.NewGuid();
        var start = DateTime.UtcNow.Date;
        var end = start.AddDays(30);
        _mockRepository.Setup(x => x.GetByDateRangeAsync(propertyId, start, end))
            .ReturnsAsync([]);

        var result = await _service.GetCalendarAsync(propertyId, start, end);

        Assert.Empty(result);
        _mockRepository.Verify(x => x.CancelExpiredPendingDirectBookingsAsync(propertyId, 15), Times.Once);
        _mockRepository.Verify(x => x.GetByDateRangeAsync(propertyId, start, end), Times.Once);
    }

    [Fact]
    public async Task UpdateBookingAsync_WithPastUnchangedCheckIn_AllowsCheckoutStatusUpdate()
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var checkIn = DateTime.UtcNow.Date.AddDays(-2);
        var checkOut = DateTime.UtcNow.Date;
        var existing = new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = BookingStatus.CheckedIn,
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };
        var update = new Booking
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = BookingStatus.CheckedOut,
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };

        _mockRepository.Setup(x => x.GetByIdAsync(bookingId)).ReturnsAsync(existing);
        _mockRepository.Setup(x => x.UpdateAsync(update)).ReturnsAsync(update);

        var result = await _service.UpdateBookingAsync(update);

        Assert.Equal(BookingStatus.CheckedOut, result.Status);
        _mockRepository.Verify(x => x.UpdateAsync(update), Times.Once);
    }

    [Fact]
    public async Task UpdateBookingAsync_CancelledBooking_ThrowsDomainRuleException()
    {
        var (existing, update) = CreateFutureBookingPair(BookingStatus.Cancelled);
        _mockRepository.Setup(x => x.GetByIdAsync(existing.Id)).ReturnsAsync(existing);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() => _service.UpdateBookingAsync(update));

        Assert.Equal("booking_update_invalid", ex.Code);
        Assert.Equal("BookingUpdateInvalid", ex.MessageKey);
        _mockRepository.Verify(x => x.UpdateAsync(It.IsAny<Booking>()), Times.Never);
    }

    [Fact]
    public async Task UpdateBookingAsync_OverlappingBooking_ThrowsDomainConflictException()
    {
        var (existing, update) = CreateFutureBookingPair(BookingStatus.Confirmed);
        _mockRepository.Setup(x => x.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        _mockRepository
            .Setup(x => x.UpdateAsync(update))
            .ThrowsAsync(new InvalidOperationException("Property not available for selected dates"));

        var ex = await Assert.ThrowsAsync<DomainConflictException>(() => _service.UpdateBookingAsync(update));

        Assert.Equal("booking_dates_unavailable", ex.Code);
        Assert.Equal("BookingDatesUnavailable", ex.MessageKey);
    }

    [Fact]
    public async Task UpdateBookingAsync_RepositoryFailsForOtherReason_PropagatesOriginalException()
    {
        var (existing, update) = CreateFutureBookingPair(BookingStatus.Confirmed);
        _mockRepository.Setup(x => x.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        _mockRepository
            .Setup(x => x.UpdateAsync(update))
            .ThrowsAsync(new InvalidOperationException("The instance of entity type 'Booking' cannot be tracked"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.UpdateBookingAsync(update));
    }

    private static (Booking Existing, Booking Update) CreateFutureBookingPair(BookingStatus existingStatus)
    {
        var bookingId = Guid.NewGuid();
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var checkIn = DateTime.UtcNow.Date.AddDays(10);
        var checkOut = checkIn.AddDays(3);
        Booking Create(BookingStatus status) => new()
        {
            Id = bookingId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = checkIn,
            CheckOutDate = checkOut,
            Status = status,
            TotalPrice = 500m,
            NumberOfGuests = 2,
        };

        return (Create(existingStatus), Create(BookingStatus.Confirmed));
    }
}
