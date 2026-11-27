using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs;

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

    [Required(ErrorMessage = "PhoneRequired")]
    [MinLength(10, ErrorMessage = "PhoneTooShort")]
    [MaxLength(20)]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "CountryRequired")]
    [MinLength(2, ErrorMessage = "CountryRequired")]
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;
}

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

    [Range(0, 99)]
    public int NumberOfChildren { get; set; }

    public List<int>? ChildrenAges { get; set; }

    [Required(ErrorMessage = "GuestInfoRequired")]
    public CreateBookingGuestRequest Guest { get; set; } = null!;

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, NumberOfGuests, NumberOfChildren, ChildrenAges);
}
