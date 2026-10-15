using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs;

public class CreateDirectBookingGuestRequest
{
    [Required(ErrorMessage = "FirstNameRequired")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "LastNameRequired")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "EmailRequired")]
    [EmailAddress(ErrorMessage = "InvalidEmail")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "CountryRequired")]
    [MaxLength(100)]
    public string Country { get; set; } = string.Empty;
}

public class CreateDirectBookingConsentRequest
{
    [Required]
    public bool DataProcessing { get; set; }

    [Required(ErrorMessage = "ConsentVersionRequired")]
    [MaxLength(100)]
    public string ConsentVersion { get; set; } = string.Empty;
}

public class CreateDirectBookingRequest : IValidatableObject
{
    [Required(ErrorMessage = "PropertyRequired")]
    public Guid PropertyId { get; set; }

    [Required(ErrorMessage = "CheckInDateRequired")]
    public DateTime CheckInDate { get; set; }

    [Required(ErrorMessage = "CheckOutDateRequired")]
    public DateTime CheckOutDate { get; set; }

    [Range(1, 100, ErrorMessage = "AdultsMinimum")]
    public int NumberOfAdults { get; set; }

    [Range(0, 100, ErrorMessage = "ChildrenNotNegative")]
    public int NumberOfChildren { get; set; }

    [Required(ErrorMessage = "GuestInfoRequired")]
    public CreateDirectBookingGuestRequest Guest { get; set; } = null!;

    [Required(ErrorMessage = "ConsentRequired")]
    public CreateDirectBookingConsentRequest Consent { get; set; } = null!;

    [MaxLength(1000)]
    public string? SpecialRequests { get; set; }

    /// <summary>Payment option: Immediate, OnCancellationDeadline, or OnSite.</summary>
    [Required]
    [EnumDataType(typeof(PaymentOption), ErrorMessage = "InvalidPaymentOption")]
    public PaymentOption PaymentOption { get; set; } = PaymentOption.Immediate;

    /// <summary>
    /// Age of each minor at check-in (0-17), one per child. Required by the server (422
    /// <c>tourist_tax_child_ages_required</c>) only when the tourist tax of the comune depends on it (BK-03).
    /// </summary>
    public List<int>? ChildrenAges { get; set; }

    /// <summary>
    /// The guest ticked the optional "send me offers" box (DB-03). Optional, <c>false</c> when left out or <c>null</c>,
    /// never required and never a condition of the booking; the box is never preselected. Offered only when the public org carries a
    /// <c>marketingConsentVersion</c> (<c>Gdpr:MarketingConsentVersion</c>, CO-15), which is the version recorded with the
    /// consent; <c>true</c> without one is refused with 422 <c>direct_booking_marketing_consent_unavailable</c>. Recorded on
    /// the guest of the booking (<c>Guest.MarketingConsent</c>) and in the register of their consents.
    /// </summary>
    public bool? MarketingConsent { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ChildrenAgesValidation.Validate(validationContext, ChildrenAges, NumberOfChildren);
}
