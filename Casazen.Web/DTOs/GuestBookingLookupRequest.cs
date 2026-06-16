using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

public class GuestBookingLookupRequest
{
    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;
}
