using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

public record GuestBookingItem(
    Guid BookingId,
    string PropertyName,
    string PropertyCity,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    BookingStatus Status,
    PaymentOption PaymentOption,
    DateTime FreeRefundDeadline
);

public record GuestBookingLookupResponse(
    List<GuestBookingItem> Bookings
);
