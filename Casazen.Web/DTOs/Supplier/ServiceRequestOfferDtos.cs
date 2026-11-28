using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

public class SupplierRequestOfferLineRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteLineLabelRequired")]
    [MaxLength(ServiceRequestLimits.QuoteLineLabelMaxLength, ErrorMessage = "ServiceRequestQuoteLineLabelTooLong")]
    public string Label { get; set; } = string.Empty;

    [Range(1, ServiceRequestOfferRules.MaxQuantity, ErrorMessage = "ServiceRequestOfferQuantityOutOfRange")]
    public int Quantity { get; set; } = 1;

    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int UnitAmountCents { get; set; }
}

public class SendSupplierRequestOfferRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MinLength(1, ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MaxLength(ServiceRequestLimits.MaxQuoteLines, ErrorMessage = "ServiceRequestQuoteTooManyLines")]
    public List<SupplierRequestOfferLineRequest>? Lines { get; set; }

    [Required(ErrorMessage = "ServiceRequestOfferStartRequired")]
    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    [MaxLength(ServiceRequestLimits.QuoteNoteMaxLength, ErrorMessage = "ServiceRequestQuoteNoteTooLong")]
    public string? Note { get; set; }
}

public class ServiceRequestOfferLineDto
{
    public string Label { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int UnitAmountCents { get; set; }

    public int AmountCents { get; set; }
}

public class ServiceRequestOfferDto
{
    public DateTime SentAt { get; set; }

    public List<ServiceRequestOfferLineDto> Lines { get; set; } = [];

    public int TotalCents { get; set; }

    public string Currency { get; set; } = "EUR";

    public string? Note { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    public DateTime? ValidUntil { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PaymentRequired { get; set; }
}

public class SupplierRequestOfferResponse
{
    public Guid RequestId { get; set; }

    public string Status { get; set; } = string.Empty;

    public string Negotiation { get; set; } = string.Empty;

    public ServiceRequestOfferDto? Offer { get; set; }
}

public static class ServiceRequestOfferMapper
{
    public static ServiceRequestOfferDto ToDto(ServiceRequestOffer offer, bool? paymentRequired = null) =>
        new()
        {
            PaymentRequired = paymentRequired,
            SentAt = AsUtc(offer.SentAt),
            Lines = offer.Lines
                .Select(line => new ServiceRequestOfferLineDto
                {
                    Label = line.Label,
                    Quantity = line.Quantity ?? 1,
                    UnitAmountCents = line.UnitAmountCents ?? line.AmountCents,
                    AmountCents = line.AmountCents,
                })
                .ToList(),
            TotalCents = offer.TotalCents,
            Note = offer.Note,
            StartUtc = AsUtc(offer.StartUtc),
            EndUtc = AsUtc(offer.EndUtc),
            StartLocal = RomeCalendar.ToRome(offer.StartUtc),
            EndLocal = RomeCalendar.ToRome(offer.EndUtc),
            ValidUntil = offer.ValidUntil is { } validUntil ? AsUtc(validUntil) : null,
        };

    public static SupplierRequestOfferResponse ToResponse(ServiceRequest request)
    {
        var offer = ServiceRequestOfferRules.PendingOf(request);
        return new SupplierRequestOfferResponse
        {
            RequestId = request.Id,
            Status = request.Status.ToString(),
            Negotiation = offer is not null ? ServiceRequestNegotiation.OfferSent : ServiceRequestNegotiation.InConversation,
            Offer = offer is null ? null : ToDto(offer),
        };
    }

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
