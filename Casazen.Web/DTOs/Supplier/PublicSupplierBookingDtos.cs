using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The booking from a supplier's public showcase (SP-10), written by a customer with no account. The requests carry personal data
// (name, e-mail, phone, address); the answers carry none: only the id of the hold, when it lapses and, once the e-mail is
// checked, what the customer needs to read on the page that follows the link. Nothing here is ever sent back to anybody else.

/// <summary>
/// Body of <c>POST api/public/suppliers/{slug}/bookings</c>. The attributes only stop an oversized body (400
/// <c>validation_error</c>); every value is checked by <see cref="ShowcaseBookingRules"/> (422 <c>supplier_booking_invalid</c>
/// with the fields at fault) and the choices of the estimate against the service (422 <c>supplier_quote_invalid</c>).
/// </summary>
public class CreatePublicSupplierBookingRequest
{
    /// <summary>
    /// The id the page made up for this attempt: sending it again is the same booking (a retry after a lost answer takes no
    /// second slot and sends no second e-mail). A new attempt, with other choices, is a new id.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>Slug of the published service (from <c>GET …/services</c>).</summary>
    [MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string? Service { get; set; }

    /// <summary>The start of the slot the customer picked, exactly as <c>GET …/slots</c> gave it (<c>startUtc</c>).</summary>
    public DateTime? StartUtc { get; set; }

    /// <summary>Units of the service's own price, as in the estimate (<c>POST …/quote</c>).</summary>
    public int? Quantity { get; set; }

    /// <summary>Surface of the home in square meters, as in the estimate.</summary>
    public int? SurfaceSqm { get; set; }

    /// <summary>The supplements picked, as in the estimate.</summary>
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

    /// <summary>The comune where the work is. The supplier covers comuni: outside them the customer asks for a quote instead.</summary>
    [MaxLength(ShowcaseBookingLimits.CityMaxLength)]
    public string? City { get; set; }

    /// <summary>Five digits.</summary>
    [MaxLength(10)]
    public string? PostalCode { get; set; }

    /// <summary>Street and number. The supplier sees it only after taking the request.</summary>
    [MaxLength(ShowcaseBookingLimits.AddressMaxLength)]
    public string? Address { get; set; }

    /// <summary>Floor and apartment (optional). The supplier sees it only after taking the request.</summary>
    [MaxLength(ShowcaseBookingLimits.FloorMaxLength)]
    public string? Floor { get; set; }

    /// <summary>A note for the access: doorbell, keys (optional). The supplier sees it only after taking the request.</summary>
    [MaxLength(ShowcaseBookingLimits.AccessNotesMaxLength + 100)]
    public string? AccessNotes { get; set; }

    /// <summary>Name and surname. The supplier sees "Nome C." until it takes the request.</summary>
    [MaxLength(ShowcaseBookingLimits.FullNameMaxLength + 100)]
    public string? FullName { get; set; }

    /// <summary>The address the e-mail check, the receipt and every later e-mail go to.</summary>
    [MaxLength(ShowcaseBookingLimits.EmailMaxLength + 50)]
    public string? Email { get; set; }

    /// <summary>A phone number, with or without spaces.</summary>
    [MaxLength(60)]
    public string? Phone { get; set; }

    /// <summary>The language of the e-mails: <c>it</c> or <c>en</c> (anything else is Italian).</summary>
    [MaxLength(16)]
    public string? Locale { get; set; }

    /// <summary>The customer accepted the privacy notice.</summary>
    public bool PrivacyAccepted { get; set; }

    /// <summary>The version of the notice the page showed (<c>Suppliers:Showcase:PrivacyNoticeVersion</c>); another one is refused (422).</summary>
    [MaxLength(ShowcaseBookingLimits.PrivacyNoticeVersionMaxLength + 50)]
    public string? PrivacyNoticeVersion { get; set; }

    /// <summary>
    /// A trap for robots: a field the page never shows (hidden by CSS, not by <c>type="hidden"</c>) and a person never fills. Anything
    /// in it and the booking is answered as if it were made, and nothing is done. No CAPTCHA (decision D9).
    /// </summary>
    [MaxLength(200)]
    public string? Website { get; set; }
}

/// <summary>Answer of <c>POST api/public/suppliers/{slug}/bookings</c> (201): the hold that waits for the e-mail check.</summary>
public class PublicSupplierBookingHoldResponse
{
    /// <summary>The id of the hold: the link of the e-mail carries it, and <c>confirm-email</c> takes it.</summary>
    public Guid Id { get; set; }

    /// <summary>When the hold lapses (UTC): the slot is free again and the link no longer works.</summary>
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
    /// <summary>The total that is blocked, in cents of euro: the total the customer saw.</summary>
    public int AmountCents { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>
    /// The client secret of the Stripe PaymentIntent, for the Payment Element; <c>null</c> when the card is already blocked
    /// (<see cref="Authorized"/>) or can no longer be asked for this block.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>The platform's publishable key; <c>null</c> together with <see cref="ClientSecret"/>.</summary>
    public string? PublishableKey { get; set; }

    /// <summary>The card is already blocked: only the e-mail is left to check.</summary>
    public bool Authorized { get; set; }
}

/// <summary>Body of <c>POST api/public/suppliers/{slug}/bookings/{id}/confirm-email</c>: the token of the link.</summary>
public class ConfirmPublicSupplierBookingRequest
{
    /// <summary>The token of the e-mail link. Single use; works until the hold lapses.</summary>
    [Required(ErrorMessage = "SupplierBookingTokenRequired")]
    [MaxLength(ShowcaseBookingLimits.TokenMaxLength, ErrorMessage = "SupplierBookingTokenRequired")]
    public string? Token { get; set; }
}

/// <summary>
/// Answer of <c>POST …/bookings/{id}/confirm-email</c> (200): the booking once the e-mail is checked, for the page that follows the
/// link. A second click on the link answers the same, with <c>alreadyConfirmed</c> and the status as it is by then.
/// </summary>
public class PublicSupplierBookingConfirmationResponse
{
    /// <summary>The code of the booking as people read it (<c>XXXXX-XXXXX</c>): with the e-mail address, it finds the request again.</summary>
    public string PublicCode { get; set; } = string.Empty;

    /// <summary>
    /// <c>Richiesto</c> the first time (the supplier has to accept); the status by then on a later click. <c>AwaitingPayment</c> (SP-15c-a) when the
    /// e-mail is checked but the customer's card is not blocked yet: the request does not exist, the supplier knows nothing, and it is created as soon
    /// as the card is blocked (see <see cref="AwaitingPayment"/>).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The name of the service.</summary>
    public string ServiceName { get; set; } = string.Empty;

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    /// <summary>The start on the clock of Rome, with its offset (the two passes of the hour that happens twice are told apart).</summary>
    public DateTimeOffset StartLocal { get; set; }

    /// <summary>The end on the clock of Rome.</summary>
    public DateTimeOffset EndLocal { get; set; }

    /// <summary>
    /// The instant the supplier has to answer by, while the request is new; after it the request is cancelled and the customer
    /// is told. <c>null</c> once the supplier answered.
    /// </summary>
    public DateTime? RespondBy { get; set; }

    /// <summary>True when the e-mail had already been checked: nothing was done again.</summary>
    public bool AlreadyConfirmed { get; set; }

    /// <summary>
    /// SP-15c-a: the e-mail is checked but the customer's card is not blocked yet, so the request is not created. The page asks the card for the block
    /// (the one <c>payment</c> of the booking gave) or, if the customer already did, asks again in a moment: the request exists when the block succeeds.
    /// Left out when false, so the answer of a booking that is not paid inside CasaZen is the one it always was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AwaitingPayment { get; set; }
}

/// <summary>Maps the booking to the API DTOs.</summary>
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

    /// <summary>The <c>status</c> of a booking whose e-mail is checked but whose card is not blocked yet: there is no request, so no request status.</summary>
    public const string AwaitingPaymentStatus = "AwaitingPayment";

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTimeOffset InRome(DateTime utc) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(AsUtc(utc)), RomeCalendar.TimeZone);
}
