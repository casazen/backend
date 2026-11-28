using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

public class CreateOtaStayRequest
{
    [Required(ErrorMessage = "OtaStayFirstNameRequired")]
    [MaxLength(100, ErrorMessage = "OtaStayNameTooLong")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "OtaStayLastNameRequired")]
    [MaxLength(100, ErrorMessage = "OtaStayNameTooLong")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "OtaStayEmailRequired")]
    [EmailAddress(ErrorMessage = "OtaStayEmailInvalid")]
    [MaxLength(255, ErrorMessage = "OtaStayEmailInvalid")]
    public string Email { get; set; } = string.Empty;

    [Range(1, 100, ErrorMessage = "OtaStayNumberOfGuestsInvalid")]
    public int? NumberOfGuests { get; set; }

    [Range(0d, 1_000_000d, ErrorMessage = "OtaStayTotalPriceInvalid")]
    public decimal? TotalPrice { get; set; }

    /// <summary>
    /// OTA of the reservation (<c>Airbnb</c>, <c>BookingCom</c>, <c>Expedia</c>, <c>Vrbo</c>, <c>TripAdvisor</c>,
    /// <c>Agoda</c>), required only when the feed's channel is "other" (422 <c>ota_stay_source_required</c>); ignored for
    /// an Airbnb or Booking.com feed.
    /// </summary>
    public BookingSource? Source { get; set; }
}

public class ResolveOtaReviewRequest
{
    public bool ApplyChannelDates { get; set; }
}
