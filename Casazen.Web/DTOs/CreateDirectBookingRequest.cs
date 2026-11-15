using System.ComponentModel.DataAnnotations;
using Casazen.Core.Bookings;
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

/// <summary>
/// Where the guest of a checkout came from (E27/F01/US01/T01, NF-123): the <c>utm_*</c> of the link the guest followed (<c>source</c>, <c>medium</c>,
/// <c>campaign</c>) and the page the guest came from. Every field is optional and none is ever refused: the server keeps only the values that pass
/// <see cref="BookingAttribution.Sanitize"/> (lower-case tokens that fit the columns, the referrer reduced to its host name) and drops the others, so a stray
/// parameter of a link never costs the guest the booking. No personal data is kept: an e-mail address, a phone number, an address of a page or a query
/// string is dropped.
/// </summary>
public class CreateDirectBookingAttributionRequest
{
    /// <summary>The <c>utm_source</c> of the link (e.g. <c>agency-site</c> for a link the agency copied from CasaZen).</summary>
    public string? Source { get; set; }

    /// <summary>The <c>utm_medium</c> of the link (e.g. <c>link</c>).</summary>
    public string? Medium { get; set; }

    /// <summary>The <c>utm_campaign</c> of the link (e.g. <c>estate-2026</c>).</summary>
    public string? Campaign { get; set; }

    /// <summary>The page the guest came from (<c>document.referrer</c>) or its host name: only the host name is kept (e.g. <c>www.agenzia.it</c>).</summary>
    public string? Referrer { get; set; }

    public BookingAttributionInput ToInput() => new(Source, Medium, Campaign, Referrer);
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

    /// <summary>
    /// The guest ticked the optional box for offers by e-mail (E07/F04/US01/T02, NF-90). Optional, <c>false</c> when left out or <c>null</c>,
    /// never required, never preselected. The legacy <see cref="MarketingConsent"/> still works and counts as this channel. Recorded in the
    /// register of the guest's consents and as a consent contact of the CRM; the double opt-in e-mail follows. Like every consent it needs the
    /// <c>marketingConsentVersion</c> of the public org (422 <c>direct_booking_marketing_consent_unavailable</c> without one).
    /// </summary>
    public bool? MarketingConsentEmail { get; set; }

    /// <summary>
    /// The guest ticked the optional box for offers on WhatsApp (E07/F04/US01/T02, NF-90). Same rules as <see cref="MarketingConsentEmail"/>,
    /// and it needs the guest's phone number: 422 <c>direct_booking_whatsapp_requires_phone</c> without one that can be reached.
    /// </summary>
    public bool? MarketingConsentWhatsApp { get; set; }

    /// <summary>
    /// The version of the soft opt-in notice (art. 130 c. 4 Codice privacy) the checkout showed, as the public org gave it
    /// (<c>softOptInNoticeVersion</c>); left out when it showed none. Recorded as «notice presented» only when it is the current one: any other
    /// is 409 <c>direct_booking_soft_opt_in_notice_outdated</c>. It is not a consent and the guest ticks nothing for it.
    /// </summary>
    [MaxLength(100)]
    public string? SoftOptInNoticeVersion { get; set; }

    /// <summary>
    /// When the guest thinks they will arrive (DB-03b), optional (step 2 of the checkout): <c>HH:mm</c> (<c>15:30</c>) or <c>late</c> for after
    /// the check-in window; left out, <c>null</c> or blank when they do not say. Only a note for the host (their booking shows
    /// <c>expectedArrivalTime</c>): it changes no price and no check, and the check-in window of the property is not enforced. Anything
    /// else is refused with 422 <c>direct_booking_arrival_time_invalid</c>.
    /// </summary>
    [MaxLength(32)]
    public string? ExpectedArrivalTime { get; set; }

    /// <summary>
    /// Where the guest came from (E27/F01/US01/T01, NF-123): the <c>utm_*</c> of the link and the page the guest came from, optional. Recorded on the booking
    /// after <see cref="BookingAttribution.Sanitize"/>, and with the booking's source it tells the host the origin (<c>origin</c> of the booking: a link of
    /// the agency, <c>utm_source=agency-site</c>, is the site of the agency). It changes no price, no check and no availability, and never refuses the checkout.
    /// </summary>
    public CreateDirectBookingAttributionRequest? Attribution { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ChildrenAgesValidation.Validate(validationContext, ChildrenAges, NumberOfChildren);
}
