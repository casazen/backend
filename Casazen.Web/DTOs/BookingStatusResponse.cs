using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

public record BookingStatusResponse(
    Guid BookingId,
    BookingStatus Status,
    PaymentOption PaymentOption
);
