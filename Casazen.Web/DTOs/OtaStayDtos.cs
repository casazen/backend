using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

/// <summary>
/// <c>POST /api/ical-blocks/{blockId}/ota-stay</c> (CO-21, decision D7): the guest of the reservation the block stands
/// for. The dates are the block's; the source is the channel of its feed.
/// </summary>
public class CreateOtaStayRequest
{
    [Required(ErrorMessage = "OtaStayFirstNameRequired")]
    [MaxLength(100, ErrorMessage = "OtaStayNameTooLong")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "OtaStayLastNameRequired")]
    [MaxLength(100, ErrorMessage = "OtaStayNameTooLong")]
    public string LastName { get; set; } = string.Empty;

    /// <summary>Where the check-in link is sent (CO-09).</summary>
    [Required(ErrorMessage = "OtaStayEmailRequired")]
    [EmailAddress(ErrorMessage = "OtaStayEmailInvalid")]
    [MaxLength(255, ErrorMessage = "OtaStayEmailInvalid")]
    public string Email { get; set; } = string.Empty;

    /// <summary>Guests of the stay; 1 when omitted. At most the property's maximum (422 <c>booking_too_many_guests</c>).</summary>
    [Range(1, 100, ErrorMessage = "OtaStayNumberOfGuestsInvalid")]
    public int? NumberOfGuests { get; set; }

    /// <summary>Amount of the reservation as the channel shows it, optional: CasaZen never computes one for an OTA stay.</summary>
    [Range(0d, 1_000_000d, ErrorMessage = "OtaStayTotalPriceInvalid")]
    public decimal? TotalPrice { get; set; }

    /// <summary>
    /// OTA of the reservation (<c>Airbnb</c>, <c>BookingCom</c>, <c>Expedia</c>, <c>Vrbo</c>, <c>TripAdvisor</c>,
    /// <c>Agoda</c>), required only when the feed's channel is "other" (422 <c>ota_stay_source_required</c>); ignored for
    /// an Airbnb or Booking.com feed.
    /// </summary>
    public BookingSource? Source { get; set; }
}

/// <summary><c>POST /api/bookings/{id}/ota-review/resolve</c> (CO-21). The body may be omitted.</summary>
public class ResolveOtaReviewRequest
{
    /// <summary>Give the stay the dates its block now has on the channel before marking it verified.</summary>
    public bool ApplyChannelDates { get; set; }
}
