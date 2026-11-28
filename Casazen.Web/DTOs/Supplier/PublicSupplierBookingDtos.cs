using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The booking from a supplier's public showcase (SP-10), written by a customer with no account. The requests carry personal data
// (name, e-mail, phone, address); the answers carry none: only the id of the hold, when it lapses and, once the e-mail is
// checked, what the customer needs to read on the page that follows the link. Nothing here is ever sent back to anybody else.

public class CreatePublicSupplierBookingRequest
{
    /// <summary>
    /// The id the page made up for this attempt: sending it again is the same booking (a retry after a lost answer takes no
    /// second slot and sends no second e-mail). A new attempt, with other choices, is a new id.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string? Service { get; set; }

    public DateTime? StartUtc { get; set; }

    public int? Quantity { get; set; }

    public int? SurfaceSqm { get; set; }

    [MaxLength(PublicShowcaseLimits.QuoteMaxOptions)]
    public List<PublicQuoteOptionRequest?>? Options { get; set; }

    /// <summary>
    /// The slot is an urgent one (SP-04c: <c>startUtc</c> comes from <c>GET …/slots?urgent=true</c>, as in the estimate): the slot is
    /// judged against the urgent plan under the calendar lock (409 <c>supplier_slot_unavailable</c> when it is not free), the price
    /// carries the surcharge of the service once, and the supplier reads <c>urgency</c> <c>High</c>. 422 <c>supplier_quote_invalid</c> on
    /// the field <c>urgent</c> for a service that does not offer urgent requests. Left out or false: the booking as it always was.
    /// </summary>
    public bool Urgent { get; set; }

    /// <summary>ISTAT code of the comune, when the customer chose it from the official list (<c>GET api/comuni</c>).</summary>
    [MaxLength(10)]
    public string? ComuneIstat { get; set; }

    [MaxLength(ShowcaseBookingLimits.CityMaxLength)]
    public string? City { get; set; }

    [MaxLength(10)]
    public string? PostalCode { get; set; }

    [MaxLength(ShowcaseBookingLimits.AddressMaxLength)]
    public string? Address { get; set; }

    [MaxLength(ShowcaseBookingLimits.FloorMaxLength)]
    public string? Floor { get; set; }

    [MaxLength(ShowcaseBookingLimits.AccessNotesMaxLength + 100)]
    public string? AccessNotes { get; set; }

    [MaxLength(ShowcaseBookingLimits.FullNameMaxLength + 100)]
    public string? FullName { get; set; }

    [MaxLength(ShowcaseBookingLimits.EmailMaxLength + 50)]
    public string? Email { get; set; }

    [MaxLength(60)]
    public string? Phone { get; set; }

    [MaxLength(16)]
    public string? Locale { get; set; }

    public bool PrivacyAccepted { get; set; }

    [MaxLength(ShowcaseBookingLimits.PrivacyNoticeVersionMaxLength + 50)]
    public string? PrivacyNoticeVersion { get; set; }

    [MaxLength(200)]
    public string? Website { get; set; }
}

public class PublicSupplierBookingHoldResponse
{
    public Guid Id { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// SP-15c-a: the block of the customer's card for a booking paid inside CasaZen — what the page needs to ask for it with the Payment
    /// Element. <c>null</c> when the booking is not paid inside CasaZen (the supplier is paid as always). The booking becomes a request when
    /// the e-mail is checked <b>and</b> the card is blocked, in either order. Left out when there is none, so the answer of a booking that is not paid
    /// inside CasaZen is the one it always was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierBookingCardBlockDto? Payment { get; set; }
}

/// <summary>
/// The block of the customer's card for a booking (SP-15c-a): the total that is blocked, and the credentials of the customer's browser to ask the
/// card for it. <b>Nothing is charged</b> until the supplier accepts; if it refuses, does not answer in time, or the customer cancels, the block is
/// released. The client secret is sent once, never logged and never stored.
/// </summary>
public class PublicSupplierBookingCardBlockDto
{
    public int AmountCents { get; set; }

    public string Currency { get; set; } = "EUR";

    public string? ClientSecret { get; set; }

    public string? PublishableKey { get; set; }

    public bool Authorized { get; set; }
}

public class ConfirmPublicSupplierBookingRequest
{
    [Required(ErrorMessage = "SupplierBookingTokenRequired")]
    [MaxLength(ShowcaseBookingLimits.TokenMaxLength, ErrorMessage = "SupplierBookingTokenRequired")]
    public string? Token { get; set; }
}

public class PublicSupplierBookingConfirmationResponse
{
    public string PublicCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string ServiceName { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    public DateTime? RespondBy { get; set; }

    public bool AlreadyConfirmed { get; set; }

    /// <summary>
    /// SP-15c-a: the e-mail is checked but the customer's card is not blocked yet, so the request is not created. The page asks the card for the block
    /// (the one <c>payment</c> of the booking gave) or, if the customer already did, asks again in a moment: the request exists when the block succeeds.
    /// Left out when false, so the answer of a booking that is not paid inside CasaZen is the one it always was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AwaitingPayment { get; set; }
}

public static class PublicSupplierBookingMapper
{
    public static ShowcaseBookingInput ToInput(CreatePublicSupplierBookingRequest request, string? consentIp) =>
        new(
            request.ClientRequestId,
            request.Service,
            request.StartUtc,
            request.Quantity,
            request.SurfaceSqm,
            request.Options?.Select(option => option is null ? null : new SupplierQuoteOption(option.Code, option.Quantity)).ToList(),
            request.ComuneIstat,
            request.City,
            request.PostalCode,
            request.Address,
            request.Floor,
            request.AccessNotes,
            request.FullName,
            request.Email,
            request.Phone,
            request.Locale,
            request.PrivacyAccepted,
            request.PrivacyNoticeVersion,
            consentIp,
            request.Urgent);

    public static PublicSupplierBookingHoldResponse ToDto(ShowcaseBookingHoldResult hold) =>
        new()
        {
            Id = hold.Id,
            ExpiresAt = AsUtc(hold.ExpiresAt),
            Payment = hold.Payment is { } payment
                ? new PublicSupplierBookingCardBlockDto
                {
                    AmountCents = payment.AmountCents,
                    Currency = payment.Currency,
                    ClientSecret = payment.ClientSecret,
                    PublishableKey = payment.PublishableKey,
                    Authorized = payment.Authorized,
                }
                : null,
        };

    public static PublicSupplierBookingConfirmationResponse ToDto(ShowcaseBookingConfirmation confirmation) =>
        new()
        {
            PublicCode = BookingCodes.Format(confirmation.PublicCode),
            Status = confirmation.AwaitingPayment ? AwaitingPaymentStatus : confirmation.Status.ToString(),
            ServiceName = confirmation.ServiceName,
            StartUtc = AsUtc(confirmation.StartUtc),
            EndUtc = AsUtc(confirmation.EndUtc),
            StartLocal = InRome(confirmation.StartUtc),
            EndLocal = InRome(confirmation.EndUtc),
            RespondBy = confirmation.RespondBy is { } respondBy ? AsUtc(respondBy) : null,
            AlreadyConfirmed = confirmation.AlreadyConfirmed,
            AwaitingPayment = confirmation.AwaitingPayment,
        };

    public const string AwaitingPaymentStatus = "AwaitingPayment";

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTimeOffset InRome(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(AsUtc(utc)), RomeCalendar.TimeZone);
}
