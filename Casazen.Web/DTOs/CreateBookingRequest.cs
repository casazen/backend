using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

/// <summary>
/// Inline guest payload embedded in a booking create request (JSON: <c>guest.phone</c>).
/// </summary>
public class CreateBookingGuestRequest
{
    [Required(ErrorMessage = "FirstNameRequired")]
    [MinLength(2, ErrorMessage = "FirstNameTooShort")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "LastNameRequired")]
    [MinLength(2, ErrorMessage = "LastNameTooShort")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "EmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    /// <summary>Phone number (international format accepted; JSON property: <c>phone</c>).</summary>
    [Required(ErrorMessage = "PhoneRequired")]
    [MinLength(10, ErrorMessage = "PhoneTooShort")]
    [MaxLength(20)]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "CountryRequired")]
    [MinLength(2, ErrorMessage = "CountryRequired")]
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;
}

/// <summary>
/// Request body for creating a booking. Excludes server-managed fields
/// (<c>Id</c>, <c>OrgId</c>, <c>GuestId</c>, pricing totals).
/// </summary>
public class CreateBookingRequest : IValidatableObject
{
    [Required(ErrorMessage = "PropertyRequired")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "CheckInDateRequired")]
    public DateTime CheckInDate { get; set; }

    [Required(ErrorMessage = "CheckOutDateRequired")]
    public DateTime CheckOutDate { get; set; }

    [Range(1, 100, ErrorMessage = "GuestsCountRange")]
    public int NumberOfGuests { get; set; }

    /// <summary>
    /// Minors among <see cref="NumberOfGuests"/> (PC-07). 0 when omitted: every guest then counts as an adult for the
    /// tourist tax, as before.
    /// </summary>
    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    /// <summary>Age of each minor at check-in (0-17), asked when the tourist tax of the comune depends on it (BK-03).</summary>
    public List<int>? ChildrenAges { get; set; }

    [Required(ErrorMessage = "GuestInfoRequired")]
    public CreateBookingGuestRequest Guest { get; set; } = null!;

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}
