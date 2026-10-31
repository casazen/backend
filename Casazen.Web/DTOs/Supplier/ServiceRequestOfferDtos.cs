using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The offer of a supplier on a request on measure from its public showcase (SP-10b-b), as the supplier sends it and the two parties read it. It is
// the quote of SP-04b under the vocabulary of the conversation (decision SP-D30: one engine): the version of the offer is the instant the supplier
// sent it, the customer repeats it when it answers, and an answer to an offer that was replaced or withdrawn meanwhile is 409
// service_request_offer_changed with nothing changed.

/// <summary>One line of the offer the supplier sends: what it is for, how many and the price of one, in cents of euro (gross, as declared).</summary>
public class SupplierRequestOfferLineRequest
{
    /// <summary>What the line is for: required, at most 120 characters.</summary>
    [Required(ErrorMessage = "ServiceRequestQuoteLineLabelRequired")]
    [MaxLength(ServiceRequestLimits.QuoteLineLabelMaxLength, ErrorMessage = "ServiceRequestQuoteLineLabelTooLong")]
    public string Label { get; set; } = string.Empty;

    /// <summary>How many (hours, rooms, pieces): from 1 to 999.</summary>
    [Range(1, ServiceRequestOfferRules.MaxQuantity, ErrorMessage = "ServiceRequestOfferQuantityOutOfRange")]
    public int Quantity { get; set; } = 1;

    /// <summary>The price of one, in cents of euro (1 to 10,000,000). The amount of the line is <c>quantity × unitAmountCents</c>, computed by the server.</summary>
    [Range(1, ServiceRequestLimits.MaxAmountCents, ErrorMessage = "ServiceRequestAmountOutOfRange")]
    public int UnitAmountCents { get; set; }
}

/// <summary>
/// Body of <c>POST api/supplier/requests/{id}/offer</c> (SP-10b-b): the supplier answers a new request on measure with an offer. A new offer
/// replaces the one that waits. The time is judged by the supplier's agenda (409 <c>supplier_slot_unavailable</c> /
/// <c>supplier_commitment_conflict</c>) but not held: it is judged again when the customer accepts.
/// </summary>
public class SendSupplierRequestOfferRequest
{
    /// <summary>The lines of the offer, from 1 to 20; their amounts add up to the total.</summary>
    [Required(ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MinLength(1, ErrorMessage = "ServiceRequestQuoteLinesRequired")]
    [MaxLength(ServiceRequestLimits.MaxQuoteLines, ErrorMessage = "ServiceRequestQuoteTooManyLines")]
    public List<SupplierRequestOfferLineRequest>? Lines { get; set; }

    /// <summary>When the work would start (UTC instant, whole minutes): required.</summary>
    [Required(ErrorMessage = "ServiceRequestOfferStartRequired")]
    public DateTime? StartUtc { get; set; }

    /// <summary>
    /// When the work would end (after the start, whole minutes). Left out, the end is the start plus the duration of the service the request names;
    /// a request that names none needs it (422 <c>service_request_time_invalid</c>).
    /// </summary>
    public DateTime? EndUtc { get; set; }

    /// <summary>An optional note for the customer: at most 500 characters.</summary>
    [MaxLength(ServiceRequestLimits.QuoteNoteMaxLength, ErrorMessage = "ServiceRequestQuoteNoteTooLong")]
    public string? Note { get; set; }
}

/// <summary>One line of an offer as the API gives it.</summary>
public class ServiceRequestOfferLineDto
{
    public string Label { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int UnitAmountCents { get; set; }

    /// <summary><c>quantity × unitAmountCents</c>.</summary>
    public int AmountCents { get; set; }
}

/// <summary>
/// The offer that waits for the customer's answer. Times are UTC instants with their Rome offset next to them. The work is not reserved: the time is
/// judged again when the customer accepts, and a time that is gone is 409 <c>supplier_slot_unavailable</c> with the offer still standing.
/// </summary>
public class ServiceRequestOfferDto
{
    /// <summary>When the supplier sent it: the version of the offer, which the customer's answer repeats (<c>offerSentAt</c>).</summary>
    public DateTime SentAt { get; set; }

    public List<ServiceRequestOfferLineDto> Lines { get; set; } = [];

    /// <summary>What the lines add up to: the price agreed if the customer accepts.</summary>
    public int TotalCents { get; set; }

    public string Currency { get; set; } = "EUR";

    /// <summary>The note of the supplier for the customer.</summary>
    public string? Note { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    /// <summary>The instant the customer has to answer by; after it the request is cancelled by CasaZen.</summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>
    /// The customer's reading only (left out for the supplier): accepting the offer is paid inside CasaZen — the customer's card is blocked for
    /// <c>totalCents</c> and the request is taken when it is (<c>negotiation</c> <c>AwaitingPayment</c> meanwhile). <c>false</c>: the offer is taken
    /// at once and the supplier is paid outside CasaZen, as for a quote.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? PaymentRequired { get; set; }
}

/// <summary>Answer of <c>POST api/supplier/requests/{id}/offer</c> and <c>…/offer/withdraw</c> (SP-10b-b).</summary>
public class SupplierRequestOfferResponse
{
    public Guid RequestId { get; set; }

    /// <summary>The status of the request: <c>Richiesto</c> (the offer and the withdrawal leave it new).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary><c>OfferSent</c> after an offer; <c>InConversation</c> after it was taken back.</summary>
    public string Negotiation { get; set; } = string.Empty;

    /// <summary>The offer that waits; <c>null</c> after it was taken back.</summary>
    public ServiceRequestOfferDto? Offer { get; set; }
}

/// <summary>Maps the offer to the API DTOs, for the supplier and for the customer.</summary>
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

    /// <summary>The answer to a supplier who sent or took back an offer: the request as it is now.</summary>
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
