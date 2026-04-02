using Casazen.Core.Entities;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Services;
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
        _service = new BookingService(_mockRepository.Object, new Mock<ILogger<BookingService>>().Object);
    }

    [Fact]
    public async Task CreateBookingAsync_WithValidBooking_ReturnsCreatedBooking()
    {
        // Arrange
        var booking = new Booking
        {
            PropertyId = Guid.NewGuid(),
            GuestId = Guid.NewGuid(),
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(5),
            TotalPrice = 500m,
            NumberOfGuests = 2
        };
        _mockRepository.Setup(x => x.AddAsync(It.IsAny<Booking>())).ReturnsAsync(booking);
        _mockRepository.Setup(x => x.IsAvailableAsync(
            It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);

        // Act
        var result = await _service.CreateBookingAsync(booking);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(booking.PropertyId, result.PropertyId);
        _mockRepository.Verify(x => x.AddAsync(It.IsAny<Booking>()), Times.Once);
        _mockRepository.Verify(x => x.IsAvailableAsync(
            booking.PropertyId, booking.CheckInDate, booking.CheckOutDate), Times.Once);
    }

    [Fact]
    public async Task IsPropertyAvailableAsync_WithAvailableProperty_ReturnsTrue()
    {
        // Arrange
        var propertyId = Guid.NewGuid();
        var checkIn = DateTime.Now.AddDays(10);
        var checkOut = DateTime.Now.AddDays(15);
        _mockRepository.Setup(x => x.IsAvailableAsync(propertyId, checkIn, checkOut)).ReturnsAsync(true);

        // Act
        var result = await _service.IsPropertyAvailableAsync(propertyId, checkIn, checkOut);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CancelBookingAsync_WithValidBooking_CancelsBooking()
    {
        // Arrange
        var bookingId = Guid.NewGuid();
        var booking = new Booking { Id = bookingId, Status = BookingStatus.Confirmed };
        _mockRepository.Setup(x => x.GetByIdAsync(bookingId)).ReturnsAsync(booking);
        _mockRepository.Setup(x => x.UpdateAsync(It.IsAny<Booking>())).ReturnsAsync(booking);

        // Act
        var result = await _service.CancelBookingAsync(bookingId);

        // Assert
        Assert.True(result);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
    }
}
