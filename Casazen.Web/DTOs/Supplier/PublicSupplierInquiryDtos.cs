using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class CreatePublicSupplierInquiryRequest
{
    /// <summary>
    /// The id the page made up for this attempt: sending it again is the same inquiry (a retry after a lost answer sends no second
    /// e-mail). A new attempt, with other words, is a new id.
    /// </summary>
    public Guid ClientRequestId { get; set; }

    [MaxLength(SupplierServiceCatalogLimits.SlugMaxLength)]
    public string? Service { get; set; }

    /// <summary>ISTAT code of the comune, when the customer chose it from the official list (<c>GET api/comuni</c>).</summary>
    [MaxLength(10)]
    public string? ComuneIstat { get; set; }

    [MaxLength(ShowcaseBookingLimits.CityMaxLength)]
    public string? City { get; set; }

    [MaxLength(ShowcaseInquiryLimits.MessageMaxLength * 2)]
    public string? Message { get; set; }

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

public class PublicSupplierInquiryResponse
{
    public Guid Id { get; set; }

    public DateTime ExpiresAt { get; set; }
}

public class ConfirmPublicSupplierInquiryRequest
{
    [Required(ErrorMessage = "SupplierBookingTokenRequired")]
    [MaxLength(ShowcaseBookingLimits.TokenMaxLength, ErrorMessage = "SupplierBookingTokenRequired")]
    public string? Token { get; set; }
}

public class PublicSupplierInquiryConfirmationResponse
{
    public string PublicCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceName { get; set; }

    public DateTime? RespondBy { get; set; }

    public bool AlreadyConfirmed { get; set; }
}

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
