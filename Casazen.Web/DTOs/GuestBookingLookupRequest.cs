using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of "Le mie prenotazioni" (BK-11, A3-10): the booking site, the booking code of the confirmation email and the
/// email the booking was made with. In the body, never in the URL, so neither code nor email end up in request logs.
/// A code in a wrong format is answered like a code that does not exist.
/// </summary>
public class GuestBookingLookupRequest
{
    [Required(ErrorMessage = "GuestBookingOrgRequired")]
    [MaxLength(100, ErrorMessage = "GuestBookingOrgRequired")]
    public string OrgSlug { get; set; } = string.Empty;

    [Required(ErrorMessage = "GuestBookingCodeRequired")]
    [MaxLength(64, ErrorMessage = "GuestBookingCodeInvalid")]
    public string BookingCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "GuestBookingEmailRequired")]
    [EmailAddress(ErrorMessage = "GuestBookingEmailInvalid")]
    [MaxLength(255, ErrorMessage = "GuestBookingEmailInvalid")]
    public string Email { get; set; } = string.Empty;
}
