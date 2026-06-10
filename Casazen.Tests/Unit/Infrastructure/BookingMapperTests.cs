using Casazen.Core.Entities;
using Casazen.Web.Infrastructure;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

public class BookingMapperTests
{
    [Fact]
    public void ToResponse_MapsGuestPhoneWithoutPropertyBookingsCycle()
    {
        var propertyId = Guid.NewGuid();
        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            PropertyId = propertyId,
            Property = new Property
            {
                Id = propertyId,
                Name = "Casa Test",
                Bookings = new List<Booking>(),
            },
            Guest = new Guest
            {
                FirstName = "Mario",
                LastName = "Rossi",
                Email = "mario@example.com",
                PhoneNumber = "+393331234567",
                Country = "IT",
            },
            CheckInDate = new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc),
            CheckOutDate = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            NumberOfGuests = 2,
            TotalPrice = 360m,
            BasePrice = 330m,
            TouristTax = 30m,
            Status = BookingStatus.Pending,
            Source = BookingSource.Direct,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var dto = BookingMapper.ToResponse(booking);

        Assert.Equal(booking.Id, dto.Id);
        Assert.Equal("Casa Test", dto.PropertyName);
        Assert.Equal("Mario", dto.Guest.FirstName);
        Assert.Equal("+393331234567", dto.Guest.Phone);
        Assert.Equal("Pending", dto.Status);
        Assert.Equal("EUR", dto.Currency);
    }
}
