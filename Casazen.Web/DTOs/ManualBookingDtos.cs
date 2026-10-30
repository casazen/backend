using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// The guest of <see cref="CreateManualBookingRequest"/> (SR-06a): the first name and the email are all the host must give. The rest
/// is optional here only: the guest gives the phone, the country and the documents with the online check-in.
/// </summary>
public class ManualBookingGuestRequest
{
    [Required(ErrorMessage = "FirstNameRequired")]
    [MinLength(2, ErrorMessage = "FirstNameTooShort")]
    [MaxLength(100, ErrorMessage = "FirstNameTooLong")]
    public string FirstName { get; set; } = string.Empty;

    /// <summary>The rest of the name, when the host has it: the client splits "Maria De Luca" at the first space.</summary>
    [MaxLength(100, ErrorMessage = "LastNameTooLong")]
    public string? LastName { get; set; }

    [Required(ErrorMessage = "EmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255, ErrorMessage = "EmailTooLong")]
    public string Email { get; set; } = string.Empty;

    [MaxLength(20, ErrorMessage = "PhoneTooLong")]
    public string? Phone { get; set; }

    [MaxLength(100, ErrorMessage = "CountryTooLong")]
    public string? Country { get; set; }
}

/// <summary>
/// Body of <c>POST /api/bookings/manual</c> (SR-06a): a booking the host enters by hand, with only what it needs. Status, source,
/// prices and the tourist tax are never read from the request (unknown JSON properties are ignored): the server prices the stay.
/// The older <c>POST /api/bookings</c> is unchanged.
/// </summary>
public class CreateManualBookingRequest : IValidatableObject
{
    [Required(ErrorMessage = "PropertyRequired")]
    public Guid PropertyId { get; set; }

    /// <summary>Arrival day (<c>yyyy-MM-dd</c>).</summary>
    [Required(ErrorMessage = "CheckInDateRequired")]
    public DateTime? CheckInDate { get; set; }

    /// <summary>Departure day (<c>yyyy-MM-dd</c>), not a night.</summary>
    [Required(ErrorMessage = "CheckOutDateRequired")]
    public DateTime? CheckOutDate { get; set; }

    /// <summary>Guests of 18 or more (at least one).</summary>
    [Range(1, 100, ErrorMessage = "GuestsCountRange")]
    public int Adults { get; set; }

    /// <summary>Minors.</summary>
    [Range(0, 99)]
    public int Children { get; set; }

    /// <summary>Age of each minor at check-in (0-17), asked when the tourist tax of the comune depends on it.</summary>
    public List<int>? ChildrenAges { get; set; }

    [Required(ErrorMessage = "GuestInfoRequired")]
    public ManualBookingGuestRequest Guest { get; set; } = null!;

    /// <summary>The host's note on the stay.</summary>
    [MaxLength(1000)]
    public string? Note { get; set; }

    /// <summary>
    /// The total the host agreed with the guest, when it is not the price of the quote. The tourist tax stays the one of the quote and
    /// the lodging is what is left of the total (<see cref="ManualBookingRules.PriceOf"/>). With <see cref="BlockId"/>, the amount of
    /// the portal.
    /// </summary>
    [Range(0d, 1_000_000d, ErrorMessage = "BookingPriceOverrideInvalid")]
    public decimal? TotalPrice { get; set; }

    /// <summary>A short note on that price (at most <see cref="ManualBookingRules.PriceNoteMaxLength"/> characters); not used with <see cref="BlockId"/>.</summary>
    [MaxLength(ManualBookingRules.PriceNoteMaxLength, ErrorMessage = "BookingPriceNoteTooLong")]
    public string? PriceNote { get; set; }

    /// <summary>
    /// The block of a portal's calendar this booking completes (<c>blockId</c> of the availability answer): the stay takes the dates of
    /// the block, no second booking is created and nothing is to collect. Without it the booking is by direct contact.
    /// </summary>
    public Guid? BlockId { get; set; }

    /// <summary>The portal of the block, only for the calendar of a portal that is not Airbnb or Booking.com (422 <c>ota_stay_source_required</c> otherwise).</summary>
    public BookingSource? Source { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, Adults + Children, Children, ChildrenAges);
}

/// <summary>
/// The answer of <c>POST /api/bookings/manual</c> (SR-06a): the booking, as everywhere, and what the host should know about it.
/// </summary>
public sealed class ManualBookingResponse : BookingResponseDto
{
    /// <summary>The stay is shorter than the minimum stay of the property: the booking was created all the same, it is the host's.</summary>
    public bool BelowMinimum { get; set; }

    /// <summary>The minimum stay of the property, when it has one.</summary>
    public int? MinNights { get; set; }

    /// <summary>
    /// False when the data of the guest needed for the Alloggiati communication are missing, as they are for a booking entered like
    /// this: the guest completes them with the online check-in.
    /// </summary>
    public bool GuestDataComplete { get; set; }
}
