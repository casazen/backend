using Casazen.Core.Entities;
using Casazen.Web.DTOs;

namespace Casazen.Web.Infrastructure;

public static class BookingMapper
{
    public static BookingResponseDto ToResponse(Booking booking) => new()
    {
        Id = booking.Id,
        PropertyId = booking.PropertyId,
        PropertyName = booking.Property?.Name,
        CheckInDate = booking.CheckInDate,
        CheckOutDate = booking.CheckOutDate,
        NumberOfGuests = booking.NumberOfGuests,
        TotalPrice = booking.TotalPrice,
        BasePrice = booking.BasePrice,
        TouristTax = booking.TouristTax,
        Status = booking.Status.ToString(),
        Source = booking.Source.ToString(),
        SpecialRequests = booking.SpecialRequests,
        Guest = booking.Guest is null
            ? new BookingGuestDto()
            : new BookingGuestDto
            {
                FirstName = booking.Guest.FirstName,
                LastName = booking.Guest.LastName,
                Email = booking.Guest.Email,
                Phone = booking.Guest.PhoneNumber,
                Country = booking.Guest.Country,
            },
        CreatedAt = booking.CreatedAt,
        UpdatedAt = booking.UpdatedAt,
    };
}
