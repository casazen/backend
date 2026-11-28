using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Suppliers;

/// <summary>
/// One line of the offer as the supplier writes it (SP-10b-b): what it is for, how many, and the price of one, in cents of euro, gross as
/// declared (decision D4: no VAT split). The amount of the line is always <c>Quantity × UnitAmountCents</c>: it is computed, never typed.
/// </summary>
public sealed record ServiceRequestOfferLine(string? Label, int Quantity, int UnitAmountCents);

/// <summary>
/// The offer that waits for the customer's answer on a request on measure (SP-10b-b), as the readers give it. It is the quote of SP-04b
/// (decision SP-D30: one engine) with what a request on measure adds: the time is never missing, and the lines say how many of what.
/// </summary>
/// <param name="SentAt">When the supplier sent it: the version of the offer, which an answer repeats.</param>
/// <param name="Lines">From one to twenty lines; the amount of each is <c>Quantity × UnitAmountCents</c>.</param>
/// <param name="TotalCents">What the lines add up to: the price agreed if the customer accepts, and the amount charged if it is paid inside CasaZen.</param>
/// <param name="StartUtc">The proposed time of the work. It is not held: the planner judges it again when the customer accepts.</param>
/// <param name="ValidUntil">The instant the customer has to answer by; after it the request is cancelled by the upkeep job.</param>
public sealed record ServiceRequestOffer(
    DateTime SentAt,
    IReadOnlyList<ServiceRequestQuoteLine> Lines,
    int TotalCents,
    string? Note,
    DateTime StartUtc,
    DateTime EndUtc,
    DateTime? ValidUntil);

/// <summary>
/// The rules of the offer on a request on measure (SP-10b-b), as pure functions: what a valid offer is and when a request has one waiting. The
/// offer is stored as the quote of SP-04b (the same columns, the same one-offer-at-a-time rule, the same version, the same deadline of the
/// customer), so what the quote already guarantees holds here: the offer replaces the one that waited, holds no slot and is judged again, under
/// the supplier's calendar lock, when the customer accepts.
/// </summary>
public static class ServiceRequestOfferRules
{
    public const int MaxQuantity = 999;

    public static (IReadOnlyList<ServiceRequestQuoteLine> Lines, int TotalCents) Normalize(IReadOnlyList<ServiceRequestOfferLine>? lines)
    {
        if (lines is null || lines.Count is 0 or > ServiceRequestLimits.MaxQuoteLines)
            throw ServiceRequestOfferErrors.InvalidOffer();

        var normalized = new List<ServiceRequestQuoteLine>(lines.Count);
        long total = 0;
        foreach (var line in lines)
        {
            if (line is null)
                throw ServiceRequestOfferErrors.InvalidOffer();

            var label = line.Label?.Trim();
            if (string.IsNullOrEmpty(label)
                || label.Length > ServiceRequestLimits.QuoteLineLabelMaxLength
                || label.Any(char.IsControl)
                || line.Quantity is < 1 or > MaxQuantity
                || line.UnitAmountCents < 1)
            {
                throw ServiceRequestOfferErrors.InvalidOffer();
            }

            var amount = (long)line.Quantity * line.UnitAmountCents;
            if (!ServiceRequestPricing.IsValidAmount(amount))
                throw ServiceRequestOfferErrors.InvalidOffer();

            normalized.Add(new ServiceRequestQuoteLine(label, (int)amount, line.Quantity, line.UnitAmountCents));
            total += amount;
        }

        if (!ServiceRequestPricing.IsValidAmount(total))
            throw ServiceRequestOfferErrors.InvalidOffer();

        return (normalized, (int)total);
    }

    public static ServiceRequestOffer? PendingOf(ServiceRequest request) =>
        PendingOf(
            request.Kind,
            request.Status,
            request.QuoteSentAt,
            request.QuoteLinesJson,
            request.QuoteTotalCents,
            request.QuoteNote,
            request.QuoteScheduledStartUtc,
            request.QuoteScheduledEndUtc,
            request.ResponseDueAt);

    public static ServiceRequestOffer? PendingOf(
        ServiceRequestKind kind,
        ServiceRequestStatus status,
        DateTime? quoteSentAt,
        string? quoteLinesJson,
        int? quoteTotalCents,
        string? quoteNote,
        DateTime? quoteStartUtc,
        DateTime? quoteEndUtc,
        DateTime? responseDueAt)
    {
        if (kind != ServiceRequestKind.Custom
            || !ServiceRequestQuoteRules.IsPending(status, quoteSentAt)
            || quoteSentAt is not { } sentAt
            || quoteTotalCents is not { } total
            || quoteStartUtc is not { } start
            || quoteEndUtc is not { } end)
        {
            return null;
        }

        return new ServiceRequestOffer(
            sentAt,
            ServiceRequestJson.ReadQuoteLines(quoteLinesJson),
            total,
            string.IsNullOrWhiteSpace(quoteNote) ? null : quoteNote,
            start,
            end,
            responseDueAt);
    }

    /// <summary>
    /// True when a payment is the one made for the offer sent at <paramref name="offerSentAt"/>: for its total, and created after the supplier sent it. An offer
    /// that was replaced is newer than the payment made for the one before, so that payment is not the offer's, whatever it was for: the card the
    /// customer blocked is never charged for an offer it did not read.
    /// </summary>
    public static bool IsPaymentOfOffer(DateTime paymentCreatedAt, int paymentAmountCents, DateTime offerSentAt, int offerTotalCents) =>
        paymentAmountCents == offerTotalCents && paymentCreatedAt >= offerSentAt;

    /// <summary>
    /// True when an offer was ever sent on the request (its trace is not empty): the supplier has answered, even if it never wrote in the thread.
    /// </summary>
    public static bool HasHistory(string? quoteEventsJson) => ServiceRequestJson.ReadQuoteEvents(quoteEventsJson).Count > 0;

    /// <summary>True when the last thing that happened to an offer of the request is that the customer turned it down (the supplier did not send another since).</summary>
    public static bool LastOfferDeclined(string? quoteEventsJson) =>
        ServiceRequestJson.ReadQuoteEvents(quoteEventsJson) is { Count: > 0 } events
        && events[^1].Kind == ServiceRequestQuoteEventKinds.Rejected;

    public static bool CanSend(ServiceRequestKind kind, ServiceRequestStatus status, bool supplierActive) =>
        kind == ServiceRequestKind.Custom && status == ServiceRequestStatus.Richiesto && supplierActive;
}

/// <summary>
/// Codes (ProblemDetails <c>code</c>) and message keys (<c>SharedResources.resx</c>, Italian and English) of the offer on a request on measure
/// (SP-10b-b). Snake case, never renamed: the frontend branches on them. The other refusals are those of the quote and of the area:
/// 404 <c>supplier_booking_not_found</c> (and for the supplier the plain <c>service_request_not_found</c>) for anything that is not a request on
/// measure of the caller, 422 <c>service_request_time_invalid</c> for a time that is missing or not valid, 409 <c>supplier_slot_unavailable</c> /
/// <c>supplier_commitment_conflict</c> for a time the supplier cannot do (when the offer is made and when it is accepted),
/// <c>service_request_state_changed</c> for a race, and <c>supplier_booking_supplier_unavailable</c> for a supplier that is not active.
/// </summary>
public static class ServiceRequestOfferErrors
{
    public const string Invalid = "service_request_offer_invalid";

    public const string CannotOffer = "service_request_cannot_offer";

    public const string NoOfferToWithdraw = "service_request_no_offer";

    public const string NoOffer = "supplier_booking_no_offer";

    public const string OfferExpired = "supplier_booking_offer_expired";

    public const string OfferChanged = "service_request_offer_changed";

    public const string InvalidMessageKey = "ServiceRequestOfferInvalid";
    public const string CannotOfferMessageKey = "ServiceRequestCannotOffer";
    public const string NoOfferToWithdrawMessageKey = "ServiceRequestNoOffer";
    public const string NoOfferMessageKey = "SupplierBookingNoOffer";
    public const string OfferExpiredMessageKey = "SupplierBookingOfferExpired";
    public const string OfferChangedMessageKey = "ServiceRequestOfferChanged";

    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        InvalidMessageKey,
        CannotOfferMessageKey,
        NoOfferToWithdrawMessageKey,
        NoOfferMessageKey,
        OfferExpiredMessageKey,
        OfferChangedMessageKey,
    ];

    public static DomainRuleException InvalidOffer() =>
        new(
            Invalid,
            InvalidMessageKey,
            ServiceRequestLimits.MaxQuoteLines,
            ServiceRequestLimits.QuoteLineLabelMaxLength,
            ServiceRequestOfferRules.MaxQuantity,
            ServiceRequestLimits.MaxAmountCents / 100);

    public static DomainRuleException CannotSendOffer() => new(CannotOffer, CannotOfferMessageKey);

    public static DomainRuleException NothingToWithdraw() => new(NoOfferToWithdraw, NoOfferToWithdrawMessageKey);

    public static DomainRuleException OfferMissing() => new(NoOffer, NoOfferMessageKey);

    public static DomainRuleException OfferLapsed() => new(OfferExpired, OfferExpiredMessageKey);

    public static DomainConflictException ChangedOffer() => new(OfferChanged, OfferChangedMessageKey);
}
