using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

// The request on measure from a supplier's public showcase (SP-10b), written by a customer with no account. The request carries personal
// data (name, e-mail, phone) and free text; the answers carry none: only the id of the inquiry, when it lapses and, once the e-mail is
// checked, what the customer needs to read on the page that follows the link. Nothing here is ever sent back to anybody else.

/// <summary>
/// Body of <c>POST api/public/suppliers/{slug}/inquiries</c>. The attributes only stop an oversized body (400 <c>validation_error</c>);
/// every value is checked by <see cref="ShowcaseInquiryRules"/> (422 <c>supplier_booking_invalid</c> with the fields at fault).
/// </summary>
public class CreatePublicSupplierInquiryRequest
{
    /// <summary>
    /// The id the page made up for this attempt: sending it again is the same inquiry (a retry after a lost answer sends no second
    /// e-mail). A new attempt, with other words, is a new id.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    /// <summary>Slug of a published service the request is about (from <c>GET …/services</c>), when it is about one — a service on quote too.</summary>
    [MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string? Service { get; set; }

    /// <summary>ISTAT code of the comune, when the customer chose it from the official list (<c>GET api/comuni</c>).</summary>
    [MaxLength(10)]
    public string? ComuneIstat { get; set; }

    /// <summary>The comune where the work is. The supplier covers comuni: outside them the request is refused (422 <c>supplier_booking_outside_zone</c>).</summary>
    [MaxLength(ShowcaseBookingLimits.CityMaxLength)]
    public string? City { get; set; }

    /// <summary>What the customer needs, in its own words: 10 to 1000 characters. The first message of the conversation.</summary>
    [MaxLength(ShowcaseInquiryLimits.MessageMaxLength * 2)]
    public string? Message { get; set; }

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
    /// A trap for robots: a field the page never shows (hidden by CSS, not by <c>type="hidden"</c>) and a person never fills. Anything in
    /// it and the request is answered as if it were made, and nothing is done. No CAPTCHA (decision D9).
    /// </summary>
    [MaxLength(200)]
    public string? Website { get; set; }
}

/// <summary>Answer of <c>POST api/public/suppliers/{slug}/inquiries</c> (201): the inquiry that waits for the e-mail check.</summary>
public class PublicSupplierInquiryResponse
{
    /// <summary>The id of the inquiry: the link of the e-mail carries it, and <c>confirm-email</c> takes it.</summary>
    public Guid Id { get; set; }

    /// <summary>When the link lapses (UTC): after it the request has to be made again.</summary>
    public DateTime ExpiresAt { get; set; }
}

/// <summary>Body of <c>POST api/public/suppliers/{slug}/inquiries/{id}/confirm-email</c>: the token of the link.</summary>
public class ConfirmPublicSupplierInquiryRequest
{
    /// <summary>The token of the e-mail link (read from the fragment of the page). Single use; works until the inquiry lapses.</summary>
    [Required(ErrorMessage = "SupplierBookingTokenRequired")]
    [MaxLength(ShowcaseBookingLimits.TokenMaxLength, ErrorMessage = "SupplierBookingTokenRequired")]
    public string? Token { get; set; }
}

/// <summary>
/// Answer of <c>POST …/inquiries/{id}/confirm-email</c> (200): the request once the e-mail is checked, for the page that follows the link.
/// A second click on the link answers the same, with <c>alreadyConfirmed</c> and the status as it is by then.
/// </summary>
public class PublicSupplierInquiryConfirmationResponse
{
    /// <summary>The code of the request as people read it (<c>XXXXX-XXXXX</c>): with the e-mail address, it finds the request again.</summary>
    public string PublicCode { get; set; } = string.Empty;

    /// <summary><c>Richiesto</c> the first time (the supplier has to answer); the status by then on a later click.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The service the request is about; left out when it is about none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; set; }

    /// <summary>The instant the supplier has to answer by, while nobody answered; after it the request closes. <c>null</c> once the supplier answered.</summary>
    public DateTime? RespondBy { get; set; }

    /// <summary>True when the e-mail had already been checked: nothing was done again.</summary>
    public bool AlreadyConfirmed { get; set; }
}

/// <summary>Maps the request on measure to the API DTOs.</summary>
public static class PublicSupplierInquiryMapper
{
    public static ShowcaseInquiryInput ToInput(CreatePublicSupplierInquiryRequest request, string? consentIp) =>
        new(
            request.ClientRequestId,
            request.Service,
            request.ComuneIstat,
            request.City,
            request.Message,
            request.FullName,
            request.Email,
            request.Phone,
            request.Locale,
            request.PrivacyAccepted,
            request.PrivacyNoticeVersion,
            consentIp);

    public static PublicSupplierInquiryResponse ToDto(ShowcaseInquiryResult inquiry) =>
        new() { Id = inquiry.Id, ExpiresAt = AsUtc(inquiry.ExpiresAt) };

    public static PublicSupplierInquiryConfirmationResponse ToDto(ShowcaseInquiryConfirmation confirmation) =>
        new()
        {
            PublicCode = BookingCodes.Format(confirmation.PublicCode),
            Status = confirmation.Status.ToString(),
            ServiceName = confirmation.ServiceName,
            RespondBy = confirmation.RespondBy is { } respondBy ? AsUtc(respondBy) : null,
            AlreadyConfirmed = confirmation.AlreadyConfirmed,
        };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
