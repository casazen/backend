using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;

namespace Casazen.Web.DTOs.Supplier;

// The customer's own area of a booking made from a supplier's public showcase (SP-11), written by a customer with no account who
// proves it is the one who booked with the code of the booking and its e-mail address. The code and the address are in the BODY,
// never in the URL, so neither is in an access log. The answers carry the booking and what the customer can do with it: never the
// data of another customer, never what the supplier noted for itself, and — before the supplier took the request — never the exact
// address (ShowcaseBookingView).

public class SupplierBookingAccessRequest : IPerEmailRateLimitedRequest
{
    [Required(ErrorMessage = "SupplierBookingAccessRequired")]
    [MaxLength(PublicShowcaseLimits.SlugMaxLength + 50, ErrorMessage = "SupplierBookingAccessRequired")]
    public string? Slug { get; set; }

    [Required(ErrorMessage = "SupplierBookingAccessRequired")]
    [MaxLength(64, ErrorMessage = "SupplierBookingAccessRequired")]
    public string? Code { get; set; }

    [Required(ErrorMessage = "SupplierBookingAccessRequired")]
    [MaxLength(ShowcaseBookingLimits.EmailMaxLength + 50, ErrorMessage = "SupplierBookingAccessRequired")]
    public string? Email { get; set; }

    string? IPerEmailRateLimitedRequest.RateLimitScope => Slug;

    public ShowcaseBookingCredentials ToCredentials() => new(Slug, Code, Email);
}

public class SupplierBookingCancelRequest : SupplierBookingAccessRequest
{
    [MaxLength(ServiceRequestLimits.CancellationReasonMaxLength + 100)]
    public string? Reason { get; set; }
}

public class SupplierBookingReportProblemRequest : SupplierBookingAccessRequest
{
    [Required(ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    [MaxLength(32, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Reason { get; set; }

    [MaxLength(ServiceDisputeLimits.TextMaxLength + 200, ErrorMessage = ServiceDisputeErrors.FormInvalidMessageKey)]
    public string? Text { get; set; }
}

public class SupplierBookingRescheduleRequest : SupplierBookingAccessRequest
{
    [Required(ErrorMessage = "SupplierBookingStartRequired")]
    public DateTime? StartUtc { get; set; }
}

public class SupplierBookingMessageRequest : SupplierBookingAccessRequest
{
    [MaxLength(ShowcaseInquiryLimits.MessageMaxLength * 2)]
    public string? Body { get; set; }
}

public class SupplierBookingQuoteAnswerRequest : SupplierBookingAccessRequest
{
    [Required(ErrorMessage = "ServiceRequestQuoteSentAtRequired")]
    public DateTime? QuoteSentAt { get; set; }
}

public class SupplierBookingOfferAnswerRequest : SupplierBookingAccessRequest
{
    [Required(ErrorMessage = "ServiceRequestOfferSentAtRequired")]
    public DateTime? OfferSentAt { get; set; }
}

public class PublicSupplierBookingViewResponse
{
    public string PublicCode { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public PublicSupplierBookingServiceDto Service { get; set; } = new();

    public PublicSupplierBookingSupplierDto Supplier { get; set; } = new();

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    public PublicSupplierBookingPlaceDto Place { get; set; } = new();

    public PublicSupplierBookingPriceDto Price { get; set; } = new();

    public DateTime? RespondBy { get; set; }

    public PublicSupplierBookingProposalDto? Proposal { get; set; }

    public PublicSupplierBookingQuoteDto? Quote { get; set; }

    public PublicSupplierBookingCancellationDto? Cancellation { get; set; }

    public string? RejectionReason { get; set; }

    public PublicSupplierBookingCancellationTermsDto CancellationTerms { get; set; } = new();

    public PublicSupplierBookingActionsDto Actions { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Kind { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierBookingThreadDto? Thread { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Negotiation { get; set; }

    /// <summary>
    /// SP-10b-b: the offer the supplier sent for a request on measure and the customer has not answered (lines with their quantity and price of one, the
    /// total, the proposed time and the instant the customer has to answer by); left out when there is none, and for any other booking. The customer
    /// answers it with <c>POST …/offer/accept</c> or <c>…/offer/decline</c>, repeating <c>offer.sentAt</c>. A request on measure never has a <c>quote</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ServiceRequestOfferDto? Offer { get; set; }

    /// <summary>
    /// SP-10b-b, only in the answer of an <c>offer/accept</c> that asks for a card (<c>offer.paymentRequired</c> and the card not blocked yet): what the
    /// browser needs to block it with the Payment Element — <c>clientSecret</c> and <c>publishableKey</c>, for <c>amountCents</c> (the total of the offer).
    /// The request is not taken yet: it is when the card is blocked (the server takes it, or <c>offer/accept</c> again). Asked again for the same offer
    /// the same session comes back. <b>A credential: never in any other answer</b>, and the answer is never cached.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierBookingPaymentSessionDto? PaymentSession { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierBookingPaymentDto? Payment { get; set; }

    /// <summary>
    /// SP-10c: the other suppliers offered to the customer of a request that <b>CasaZen closed because the supplier did not answer in time</b>
    /// (<c>status</c> <c>Annullato</c>, cancelled by <c>system</c>): at most three, best first; an empty list when there is none to suggest for now.
    /// Left out for every other booking (the same list is in <c>POST …/alternatives</c>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PublicSupplierAlternativeDto>? Alternatives { get; set; }
}

public class PublicSupplierBookingAlternativesResponse
{
    public List<PublicSupplierAlternativeDto> Alternatives { get; set; } = [];
}

public class PublicSupplierAlternativeDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CoverAlt { get; set; }

    public bool Verified { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MedianResponseMinutes { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierAlternativeServiceDto? Service { get; set; }
}

public class PublicSupplierAlternativeServiceDto
{
    public string Slug { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FromPriceCents { get; set; }

    public SupplierServicePriceUnit PriceUnit { get; set; }

    public bool RequiresQuote { get; set; }
}

public static class PublicSupplierAlternativeMapper
{
    public static List<PublicSupplierAlternativeDto> ToDtos(IEnumerable<ShowcaseAlternative> alternatives) =>
        alternatives
            .Select(alternative => new PublicSupplierAlternativeDto
            {
                Name = alternative.Name,
                Slug = alternative.Slug,
                CoverUrl = alternative.CoverUrl,
                CoverAlt = alternative.CoverAlt,
                Verified = alternative.Verified,
                MedianResponseMinutes = alternative.MedianResponseMinutes,
                Service = alternative.Service is { } service
                    ? new PublicSupplierAlternativeServiceDto
                    {
                        Slug = service.Slug,
                        Name = service.Name,
                        FromPriceCents = service.FromPriceCents,
                        PriceUnit = service.PriceUnit,
                        RequiresQuote = service.RequiresQuote,
                    }
                    : null,
            })
            .ToList();
}

public class PublicSupplierBookingPaymentSessionDto
{
    /// <summary>The secret of the Stripe PaymentIntent that blocks the card (Stripe.js <c>confirmPayment</c>): a credential, sent once, never stored or logged.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    public string PublishableKey { get; set; } = string.Empty;

    public int AmountCents { get; set; }

    public string Currency { get; set; } = "EUR";
}

public class PublicSupplierBookingPaymentDto
{
    public string Status { get; set; } = string.Empty;

    public int AmountCents { get; set; }

    public string Currency { get; set; } = "EUR";

    public int RefundedCents { get; set; }

    /// <summary>
    /// While CasaZen holds the money and the work is done: when it goes to the supplier if the customer says nothing (the end of the time to
    /// speak up); otherwise <c>null</c>. The page counts down to it.
    /// </summary>
    public DateTime? ReleaseAtUtc { get; set; }

    public DateTimeOffset? ReleaseAtLocal { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicSupplierBookingDisputeDto? Dispute { get; set; }
}

public class PublicSupplierBookingDisputeDto
{
    public string Status { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public bool NoShow { get; set; }

    public bool OpenedByCasaZen { get; set; }

    public string? Text { get; set; }

    public DateTime OpenedAt { get; set; }

    public DateTime SupplierReplyDueAt { get; set; }

    public bool SupplierReplied { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int? RefundCents { get; set; }

    public bool Settled { get; set; }
}

public class PublicSupplierBookingServiceDto
{
    public string Name { get; set; } = string.Empty;

    public string? Slug { get; set; }
}

public class PublicSupplierBookingSupplierDto
{
    public string Name { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;
}

public class PublicSupplierBookingPlaceDto
{
    public string City { get; set; } = string.Empty;

    public string? PostalCode { get; set; }

    public string? Address { get; set; }

    public string? Floor { get; set; }

    public string? AccessNotes { get; set; }
}

public class PublicSupplierBookingPriceDto
{
    public string Currency { get; set; } = "EUR";

    public int? EstimatedAmountCents { get; set; }

    public int? QuotedAmountCents { get; set; }

    public int? FinalAmountCents { get; set; }

    public int? AmountCents { get; set; }

    public string? Basis { get; set; }

    public List<PublicSupplierBookingPriceLineDto> Lines { get; set; } = [];
}

public class PublicSupplierBookingPriceLineDto
{
    public string Kind { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public int UnitAmountCents { get; set; }

    public int AmountCents { get; set; }
}

public class PublicSupplierBookingProposalDto
{
    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public DateTimeOffset StartLocal { get; set; }

    public DateTimeOffset EndLocal { get; set; }

    public DateTime ProposedAt { get; set; }

    public DateTime? AnswerBy { get; set; }

    public string? Message { get; set; }
}

public class PublicSupplierBookingQuoteDto
{
    public DateTime SentAt { get; set; }

    public List<PublicSupplierBookingQuoteLineDto> Lines { get; set; } = [];

    public int TotalCents { get; set; }

    public string? Note { get; set; }

    /// <summary>Another time the quote offers instead of the booking's own; <c>null</c> when it leans on the booking's time.</summary>
    public DateTime? StartUtc { get; set; }

    public DateTime? EndUtc { get; set; }

    public DateTimeOffset? StartLocal { get; set; }

    public DateTimeOffset? EndLocal { get; set; }

    public DateTime? AnswerBy { get; set; }
}

public class PublicSupplierBookingQuoteLineDto
{
    public string Label { get; set; } = string.Empty;

    public int AmountCents { get; set; }
}

public class PublicSupplierBookingCancellationDto
{
    public DateTime At { get; set; }

    public string By { get; set; } = string.Empty;

    public string? Reason { get; set; }
}

public class PublicSupplierBookingCancellationTermsDto
{
    public DateTime FreeUntilUtc { get; set; }

    public DateTimeOffset FreeUntilLocal { get; set; }

    public bool IsFree { get; set; }
}

public class PublicSupplierBookingActionsDto
{
    public bool CanCancel { get; set; }

    public bool CanReschedule { get; set; }

    public bool CanRespondToProposal { get; set; }

    public bool CanRespondToQuote { get; set; }

    public bool CanConfirmOk { get; set; }

    public bool CanReportProblem { get; set; }

    public bool CanWithdrawProblem { get; set; }

    public bool CanRespondToOffer { get; set; }
}

public class PublicSupplierBookingThreadDto
{
    public bool CanWrite { get; set; }

    public List<PublicSupplierBookingMessageDto> Messages { get; set; } = [];
}

public class PublicSupplierBookingMessageDto
{
    public Guid Id { get; set; }

    public string Author { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }
}

public static class PublicSupplierBookingViewMapper
{
    public static PublicSupplierBookingViewResponse ToDto(ShowcaseBookingView view) =>
        new()
        {
            PublicCode = BookingCodes.Format(view.PublicCode),
            Status = view.Status.ToString(),
            Service = new PublicSupplierBookingServiceDto { Name = view.Service.Name, Slug = view.Service.Slug },
            Supplier = new PublicSupplierBookingSupplierDto { Name = view.Supplier.Name, Slug = view.Supplier.Slug },
            StartUtc = AsUtc(view.StartUtc),
            EndUtc = AsUtc(view.EndUtc),
            StartLocal = RomeCalendar.ToRome(view.StartUtc),
            EndLocal = RomeCalendar.ToRome(view.EndUtc),
            Place = new PublicSupplierBookingPlaceDto
            {
                City = view.Place.City,
                PostalCode = view.Place.PostalCode,
                Address = view.Place.Address,
                Floor = view.Place.Floor,
                AccessNotes = view.Place.AccessNotes,
            },
            Price = new PublicSupplierBookingPriceDto
            {
                EstimatedAmountCents = view.Price.EstimatedAmountCents,
                QuotedAmountCents = view.Price.QuotedAmountCents,
                FinalAmountCents = view.Price.FinalAmountCents,
                AmountCents = view.Price.AmountCents,
                Basis = view.Price.Basis,
                Lines = view.Price.Lines
                    .Select(line => new PublicSupplierBookingPriceLineDto
                    {
                        Kind = line.Kind,
                        Label = line.Label,
                        Quantity = line.Quantity,
                        UnitAmountCents = line.UnitAmountCents,
                        AmountCents = line.AmountCents,
                    })
                    .ToList(),
            },
            RespondBy = view.RespondBy is { } respondBy ? AsUtc(respondBy) : null,
            Proposal = view.Proposal is { } proposal
                ? new PublicSupplierBookingProposalDto
                {
                    StartUtc = AsUtc(proposal.StartUtc),
                    EndUtc = AsUtc(proposal.EndUtc),
                    StartLocal = RomeCalendar.ToRome(proposal.StartUtc),
                    EndLocal = RomeCalendar.ToRome(proposal.EndUtc),
                    ProposedAt = AsUtc(proposal.ProposedAt),
                    AnswerBy = proposal.AnswerBy is { } answerBy ? AsUtc(answerBy) : null,
                    Message = proposal.Message,
                }
                : null,
            Quote = view.Quote is { } quote
                ? new PublicSupplierBookingQuoteDto
                {
                    SentAt = AsUtc(quote.SentAt),
                    Lines = quote.Lines
                        .Select(line => new PublicSupplierBookingQuoteLineDto { Label = line.Label, AmountCents = line.AmountCents })
                        .ToList(),
                    TotalCents = quote.TotalCents,
                    Note = quote.Note,
                    StartUtc = quote.StartUtc is { } quoteStart ? AsUtc(quoteStart) : null,
                    EndUtc = quote.EndUtc is { } quoteEnd ? AsUtc(quoteEnd) : null,
                    StartLocal = quote.StartUtc is { } localStart ? RomeCalendar.ToRome(localStart) : null,
                    EndLocal = quote.EndUtc is { } localEnd ? RomeCalendar.ToRome(localEnd) : null,
                    AnswerBy = quote.AnswerBy is { } quoteAnswerBy ? AsUtc(quoteAnswerBy) : null,
                }
                : null,
            Cancellation = view.Cancellation is { } cancellation
                ? new PublicSupplierBookingCancellationDto
                {
                    At = AsUtc(cancellation.At),
                    By = PartyOf(cancellation.By),
                    Reason = cancellation.Reason,
                }
                : null,
            RejectionReason = view.RejectionReason,
            CancellationTerms = new PublicSupplierBookingCancellationTermsDto
            {
                FreeUntilUtc = AsUtc(view.CancellationTerms.FreeUntilUtc),
                FreeUntilLocal = RomeCalendar.ToRome(view.CancellationTerms.FreeUntilUtc),
                IsFree = view.CancellationTerms.IsFree,
            },
            Actions = new PublicSupplierBookingActionsDto
            {
                CanCancel = view.Actions.CanCancel,
                CanReschedule = view.Actions.CanReschedule,
                CanRespondToProposal = view.Actions.CanRespondToProposal,
                CanRespondToQuote = view.Actions.CanRespondToQuote,
                CanConfirmOk = view.Actions.CanConfirmOk,
                CanReportProblem = view.Actions.CanReportProblem,
                CanWithdrawProblem = view.Actions.CanWithdrawProblem,
                CanRespondToOffer = view.Actions.CanRespondToOffer,
            },
            Kind = view.Kind == ServiceRequestKind.Custom ? nameof(ServiceRequestKind.Custom) : null,
            Thread = view.Thread is { } thread
                ? new PublicSupplierBookingThreadDto
                {
                    CanWrite = thread.CanWrite,
                    Messages = thread.Messages
                        .Select(message => new PublicSupplierBookingMessageDto
                        {
                            Id = message.Id,
                            Author = AuthorOf(message.Author),
                            Body = message.Body,
                            CreatedAt = AsUtc(message.CreatedAt),
                            ReadAt = message.ReadAt is { } readAt ? AsUtc(readAt) : null,
                        })
                        .ToList(),
                }
                : null,
            Negotiation = view.Negotiation,
            Offer = view.Offer is { } offer ? ServiceRequestOfferMapper.ToDto(offer, view.OfferPaymentRequired) : null,
            PaymentSession = view.PaymentSession is { } session
                ? new PublicSupplierBookingPaymentSessionDto
                {
                    ClientSecret = session.ClientSecret,
                    PublishableKey = session.PublishableKey,
                    AmountCents = session.AmountCents,
                    Currency = session.Currency,
                }
                : null,
            Alternatives = view.Alternatives is { } alternatives ? PublicSupplierAlternativeMapper.ToDtos(alternatives) : null,
            Payment = view.Payment is { } payment
                ? new PublicSupplierBookingPaymentDto
                {
                    Status = payment.Status,
                    AmountCents = payment.AmountCents,
                    Currency = payment.Currency,
                    RefundedCents = payment.RefundedCents,
                    ReleaseAtUtc = payment.ReleaseAtUtc is { } releaseAt ? AsUtc(releaseAt) : null,
                    ReleaseAtLocal = payment.ReleaseAtUtc is { } localReleaseAt ? RomeCalendar.ToRome(localReleaseAt) : null,
                    Dispute = payment.Dispute is { } dispute
                        ? new PublicSupplierBookingDisputeDto
                        {
                            Status = dispute.Status,
                            Reason = dispute.Reason,
                            NoShow = dispute.NoShow,
                            OpenedByCasaZen = dispute.OpenedByCasaZen,
                            Text = dispute.Text,
                            OpenedAt = AsUtc(dispute.OpenedAt),
                            SupplierReplyDueAt = AsUtc(dispute.SupplierReplyDueAt),
                            SupplierReplied = dispute.SupplierReplied,
                            ResolvedAt = dispute.ResolvedAt is { } resolvedAt ? AsUtc(resolvedAt) : null,
                            RefundCents = dispute.RefundCents,
                            Settled = dispute.Settled,
                        }
                        : null,
                }
                : null,
        };

    internal static string AuthorOf(ServiceRequestMessageAuthor author) => author switch
    {
        ServiceRequestMessageAuthor.Customer => "customer",
        ServiceRequestMessageAuthor.Supplier => "supplier",
        _ => "system",
    };

    internal static string PartyOf(ServiceRequestActorParty party) => party switch
    {
        ServiceRequestActorParty.Customer => "customer",
        ServiceRequestActorParty.Supplier => "supplier",
        _ => "system",
    };

    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
