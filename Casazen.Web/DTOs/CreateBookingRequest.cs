using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

/// <summary>
/// Inline guest payload embedded in a booking create request (JSON: <c>guest.phone</c>).
/// </summary>
public class CreateBookingGuestRequest
{
    [Required(ErrorMessage = "First name is required")]
    [MinLength(2, ErrorMessage = "First name must be at least 2 characters")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required")]
    [MinLength(2, ErrorMessage = "Last name must be at least 2 characters")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email address")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Phone number (international format accepted; JSON property: <c>phone</c>).</summary>
    [Required(ErrorMessage = "Phone is required")]
    [MinLength(10, ErrorMessage = "Phone number must be at least 10 digits")]
    [MaxLength(20)]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "Country is required")]
    [MinLength(2, ErrorMessage = "Country is required")]
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;
}

/// <summary>
/// Request body for creating a booking. Excludes server-managed fields
/// (<c>Id</c>, <c>OrgId</c>, <c>GuestId</c>, pricing totals).
/// </summary>
public class CreateBookingRequest : IValidatableObject
{
    [Required(ErrorMessage = "Property is required")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "Check-in date is required")]
    public DateTime CheckInDate { get; set; }

    [Required(ErrorMessage = "Check-out date is required")]
    public DateTime CheckOutDate { get; set; }

    [Range(1, 100, ErrorMessage = "Number of guests must be between 1 and 100")]
    public int NumberOfGuests { get; set; }

    /// <summary>
    /// Minors among <see cref="NumberOfGuests"/> (PC-07). 0 when omitted: every guest then counts as an adult for the
    /// tourist tax, as before.
    /// </summary>
    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    /// <summary>Age of each minor at check-in (0-17), asked when the tourist tax of the comune depends on it (BK-03).</summary>
    public List<int>? ChildrenAges { get; set; }

    [Required(ErrorMessage = "Guest information is required")]
    public CreateBookingGuestRequest Guest { get; set; } = null!;

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}
