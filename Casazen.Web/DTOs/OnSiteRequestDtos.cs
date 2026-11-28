using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public sealed class ConfirmOnSiteRequestEmailRequest
{
    [Required(ErrorMessage = "OnSiteRequestTokenRequired")]
    [MaxLength(128, ErrorMessage = "OnSiteRequestTokenRequired")]
    public string Token { get; set; } = string.Empty;
}

public sealed record OnSiteRequestConfirmationResponse(
    Guid BookingId,
    BookingStatus Status,
    OnSiteRequestState? State,
    DateTime? RequestExpiresAt)
{
    public static OnSiteRequestConfirmationResponse From(OnSiteRequestSnapshot snapshot) =>
        new(snapshot.BookingId, snapshot.Status, snapshot.State, snapshot.RequestExpiresAt);
}

public sealed class DeclineOnSiteRequestRequest
{
    [MaxLength(500, ErrorMessage = "OnSiteRequestDeclineMessageTooLong")]
    public string? Message { get; set; }
}

public sealed record BookingApprovalRequestDto(
    Guid Id,
    Guid PropertyId,
    string PropertyName,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int Nights,
    int NumberOfGuests,
    int NumberOfAdults,
    int NumberOfChildren,
    decimal TotalPrice,
    string Currency,
    string SpecialRequests,
    BookingGuestDto Guest,
    DateTime? EmailConfirmedAt,
    DateTime? RespondBy)
{
    public static BookingApprovalRequestDto From(Booking booking) => new(
        booking.Id,
        booking.PropertyId,
        booking.Property?.Name ?? string.Empty,
        booking.CheckInDate,
        booking.CheckOutDate,
        (booking.CheckOutDate.Date - booking.CheckInDate.Date).Days,
        booking.NumberOfGuests,
        booking.NumberOfAdults,
        booking.NumberOfChildren,
        booking.TotalPrice,
        StayCurrency.ForProperty(booking.Property),
        booking.SpecialRequests,
        booking.Guest is null
            ? new BookingGuestDto()
            : new BookingGuestDto
            {
                FirstName = booking.Guest.FirstName,
                LastName = booking.Guest.LastName,
                Email = booking.Guest.Email,
                Phone = booking.Guest.PhoneNumber,
                Country = booking.Guest.Country,
            },
        booking.GuestEmailVerifiedAt,
        booking.RequestExpiresAt);
}
