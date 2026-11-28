using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public class ManualBookingGuestRequest
{
    [Required(ErrorMessage = "FirstNameRequired")]
    [MinLength(2, ErrorMessage = "FirstNameTooShort")]
    [MaxLength(100, ErrorMessage = "FirstNameTooLong")]
    public string FirstName { get; set; } = string.Empty;

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

    [Required(ErrorMessage = "CheckInDateRequired")]
    public DateTime? CheckInDate { get; set; }

    [Required(ErrorMessage = "CheckOutDateRequired")]
    public DateTime? CheckOutDate { get; set; }

    [Range(1, 100, ErrorMessage = "GuestsCountRange")]
    public int Adults { get; set; }

    [Range(0, 99)]
    public int Children { get; set; }

    public List<int>? ChildrenAges { get; set; }

    [Required(ErrorMessage = "GuestInfoRequired")]
    public ManualBookingGuestRequest Guest { get; set; } = null!;

    [MaxLength(1000)]
    public string? Note { get; set; }

    [Range(0d, 1_000_000d, ErrorMessage = "BookingPriceOverrideInvalid")]
    public decimal? TotalPrice { get; set; }

    [MaxLength(ManualBookingRules.PriceNoteMaxLength, ErrorMessage = "BookingPriceNoteTooLong")]
    public string? PriceNote { get; set; }

    public Guid? BlockId { get; set; }

    /// <summary>The portal of the block, only for the calendar of a portal that is not Airbnb or Booking.com (422 <c>ota_stay_source_required</c> otherwise).</summary>
    public BookingSource? Source { get; set; }

    /// <summary>
    /// How the guest pays (the payment step of the new-booking wizard, NF-74): <c>none</c> (the default), <c>paymentLink</c> (the guest is
    /// e-mailed a secure link to pay the balance online; needs <c>payment.write</c> and a Stripe account ready, 422
    /// <c>payment_link_unavailable</c> otherwise), <c>onSite</c> or <c>bankTransfer</c> (recorded only: the host records the income with
    /// "Registra incasso", nothing is sent). Not with <see cref="BlockId"/>: the portal collects.
    /// </summary>
    public HostPaymentArrangement? PaymentArrangement { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        BookingGuestsValidation.Validate(validationContext, Adults + Children, Children, ChildrenAges);
}

public sealed class ManualBookingResponse : BookingResponseDto
{
    public bool BelowMinimum { get; set; }

    public int? MinNights { get; set; }

    /// <summary>
    /// False when the data of the guest needed for the Alloggiati communication are missing, as they are for a booking entered like
    /// this: the guest completes them with the online check-in.
    /// </summary>
    public bool GuestDataComplete { get; set; }

    public string? PaymentLinkUrl { get; set; }

    /// <summary>
    /// Why the payment link is missing although the booking was created (<c>payment_link_unavailable</c>, <c>payment_provider_error</c>): the
    /// host sends it again from the booking. <c>null</c> otherwise.
    /// </summary>
    public string? PaymentLinkError { get; set; }
}

/// <summary>The answer of <c>POST /api/bookings/{id}/payment-link</c> (NF-74, E08/F01/US01/T05).</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="PaymentLinkUrl">The link e-mailed to the guest, given this once for the host to copy; the previous link no longer works.</param>
/// <param name="Amount">The balance the guest pays.</param>
/// <param name="Currency">The currency of the property.</param>
/// <param name="SentAt">When the link was made (UTC).</param>
/// <param name="EmailSent">The e-mail to the guest was queued (false when the guest has no address).</param>
public sealed record BookingPaymentLinkResponse(
    Guid BookingId,
    string PaymentLinkUrl,
    decimal Amount,
    string Currency,
    DateTime SentAt,
    bool EmailSent)
{
    public static BookingPaymentLinkResponse From(BookingPaymentLink link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new BookingPaymentLinkResponse(link.BookingId, link.Url, link.Amount, link.Currency, link.SentAt, link.EmailQueued);
    }
}
