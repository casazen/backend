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
    /// <summary>Most units of one line (a number of hours, rooms or pieces; a typo for a thousand is not one).</summary>
    public const int MaxQuantity = 999;

    /// <summary>
    /// The lines of an offer, trimmed, as the lines of a quote, and what they add up to. From one to <see cref="ServiceRequestLimits.MaxQuoteLines"/>
    /// lines, each with a description of at most <see cref="ServiceRequestLimits.QuoteLineLabelMaxLength"/> characters, a quantity from 1 to
    /// <see cref="MaxQuantity"/> and a unit price from 1 cent; the amount of a line and the total are at most the bound of a request.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="ServiceRequestOfferErrors.Invalid"/>.</exception>
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

    /// <summary>
    /// The offer that waits for the customer's answer on <paramref name="request"/>, or <c>null</c> when there is none: the request is a request on
    /// measure, still new, and the supplier sent an offer (with the time it proposes, which an offer always has).
    /// </summary>
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

    /// <summary>
    /// <see cref="PendingOf(ServiceRequest)"/> from the columns, for the readers that select only what they need. <paramref name="responseDueAt"/>
    /// is the deadline of the customer while an offer waits (a showcase request: the supplier answered, and the clock became the customer's).
    /// </summary>
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

    /// <summary>
    /// True when the supplier may send an offer on a request on measure: the request is new, the conversation is open and the supplier is active.
    /// (A request that is taken, refused or cancelled has no offer to send; one that is not a request on measure is answered with a quote.)
    /// </summary>
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
    /// <summary>422: the offer is not valid: not from one to twenty lines, a line without a description, with a quantity or a price outside the limits, or a total above the bound of a request.</summary>
    public const string Invalid = "service_request_offer_invalid";

    /// <summary>422: the supplier answers with an offer a request that is not waiting for its answer (taken, refused, cancelled, done).</summary>
    public const string CannotOffer = "service_request_cannot_offer";

    /// <summary>422: there is no offer to withdraw (never sent, withdrawn, answered, or the request moved on).</summary>
    public const string NoOfferToWithdraw = "service_request_no_offer";

    /// <summary>422: the booking has no offer to answer (never sent, withdrawn, answered, or the request moved on).</summary>
    public const string NoOffer = "supplier_booking_no_offer";

    /// <summary>422: the time to answer the offer has passed; the request is cancelled by the next run of the upkeep job.</summary>
    public const string OfferExpired = "supplier_booking_offer_expired";

    /// <summary>409: the supplier replaced or withdrew the offer after the customer read it, so the answer is not about the offer that waits: nothing was changed.</summary>
    public const string OfferChanged = "service_request_offer_changed";

    public const string InvalidMessageKey = "ServiceRequestOfferInvalid";
    public const string CannotOfferMessageKey = "ServiceRequestCannotOffer";
    public const string NoOfferToWithdrawMessageKey = "ServiceRequestNoOffer";
    public const string NoOfferMessageKey = "SupplierBookingNoOffer";
    public const string OfferExpiredMessageKey = "SupplierBookingOfferExpired";
    public const string OfferChangedMessageKey = "ServiceRequestOfferChanged";

    /// <summary>Every <c>SharedResources</c> key the offer raises (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } =
    [
        InvalidMessageKey,
        CannotOfferMessageKey,
        NoOfferToWithdrawMessageKey,
        NoOfferMessageKey,
        OfferExpiredMessageKey,
        OfferChangedMessageKey,
    ];

    /// <summary>422: the offer is not valid.</summary>
    public static DomainRuleException InvalidOffer() =>
        new(
            Invalid,
            InvalidMessageKey,
            ServiceRequestLimits.MaxQuoteLines,
            ServiceRequestLimits.QuoteLineLabelMaxLength,
            ServiceRequestOfferRules.MaxQuantity,
            ServiceRequestLimits.MaxAmountCents / 100);

    /// <summary>422: the request does not wait for an offer.</summary>
    public static DomainRuleException CannotSendOffer() => new(CannotOffer, CannotOfferMessageKey);

    /// <summary>422: there is no offer to take back.</summary>
    public static DomainRuleException NothingToWithdraw() => new(NoOfferToWithdraw, NoOfferToWithdrawMessageKey);

    /// <summary>422: the booking has no offer to answer.</summary>
    public static DomainRuleException OfferMissing() => new(NoOffer, NoOfferMessageKey);

    /// <summary>422: the time to answer the offer has passed.</summary>
    public static DomainRuleException OfferLapsed() => new(OfferExpired, OfferExpiredMessageKey);

    /// <summary>409: the offer is not the one the customer read.</summary>
    public static DomainConflictException ChangedOffer() => new(OfferChanged, OfferChangedMessageKey);
}
