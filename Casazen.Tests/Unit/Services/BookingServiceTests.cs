using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
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
            new Mock<IGuestService>().Object,
            new Mock<ITaxCalculationService>().Object,
            new Mock<IStripeService>().Object,
            new Mock<IPaymentRepository>().Object,
            configuration,
            new Mock<ILogger<BookingService>>().Object);
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
        _mockRepository.Verify(x => x.IsAvailableAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate, null), Times.Once);
    }

    [Fact]
    public async Task IsPropertyAvailableAsync_WithAvailableProperty_ReturnsTrue()
    {
        var propertyId = Guid.NewGuid();
        var checkIn = DateTime.Now.AddDays(10);
        var checkOut = DateTime.Now.AddDays(15);
        _mockRepository.Setup(x => x.IsAvailableAsync(propertyId, checkIn, checkOut, null)).ReturnsAsync(true);

        var result = await _service.IsPropertyAvailableAsync(propertyId, checkIn, checkOut);

        Assert.True(result);
    }

    [Fact]
    public async Task CancelBookingAsync_WithValidBooking_CancelsBooking()
    {
        var bookingId = Guid.NewGuid();
        var booking = new Booking { Id = bookingId, Status = BookingStatus.Confirmed };
        _mockRepository.Setup(x => x.GetByIdAsync(bookingId)).ReturnsAsync(booking);
        _mockRepository.Setup(x => x.UpdateAsync(It.IsAny<Booking>())).ReturnsAsync(booking);

        var result = await _service.CancelBookingAsync(bookingId);

        Assert.True(result);
        _mockRepository.Verify(x => x.UpdateAsync(It.Is<Booking>(b => b.Status == BookingStatus.Cancelled)), Times.Once);
    }
}
