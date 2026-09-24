using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.DTOs;

namespace Casazen.Web.Infrastructure;

public static class BookingMapper
{
    public static BookingResponseDto ToResponse(Booking booking) => ToResponse(booking, DateTime.UtcNow);

    /// <param name="booking">The booking, with its property and guest when loaded.</param>
    /// <param name="nowUtc">Instant that decides whether a "pay at the property" request is still open.</param>
    public static BookingResponseDto ToResponse(Booking booking, DateTime nowUtc) => new()
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
        PaymentOption = booking.PaymentOption.ToString(),
        OnSiteRequestState = OnSiteRequests.StateOf(booking, nowUtc)?.ToString(),
        RequestExpiresAt = OnSiteRequests.StateOf(booking, nowUtc) is null ? null : booking.RequestExpiresAt,
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
