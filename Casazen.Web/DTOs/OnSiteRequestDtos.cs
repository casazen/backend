using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>Body of <c>POST /api/public/bookings/{id}/confirm-email</c>: the token of the link in the "request received" email.</summary>
public sealed class ConfirmOnSiteRequestEmailRequest
{
    [Required(ErrorMessage = "OnSiteRequestTokenRequired")]
    [MaxLength(128, ErrorMessage = "OnSiteRequestTokenRequired")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// A "pay at the property" request as the guest sees it after the confirmation link (BK-06, D5): <c>state</c>
/// <c>AwaitingHostApproval</c> with the host's deadline, or the final status (Confirmed / Cancelled).
/// </summary>
public sealed record OnSiteRequestConfirmationResponse(
    Guid BookingId,
    BookingStatus Status,
    OnSiteRequestState? State,
    DateTime? RequestExpiresAt)
{
    public static OnSiteRequestConfirmationResponse From(OnSiteRequestSnapshot snapshot) =>
        new(snapshot.BookingId, snapshot.Status, snapshot.State, snapshot.RequestExpiresAt);
}

/// <summary>Body of <c>POST /api/bookings/{id}/decline</c>: an optional message written in the email to the guest.</summary>
public sealed class DeclineOnSiteRequestRequest
{
    [MaxLength(500, ErrorMessage = "OnSiteRequestDeclineMessageTooLong")]
    public string? Message { get; set; }
}

/// <summary>A "pay at the property" request waiting for the host (<c>GET /api/bookings/approval-requests</c>).</summary>
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
        "EUR",
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
