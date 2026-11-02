using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Core.Suppliers;

/// <summary>
/// One line of a quote (SP-04b): what it is for and how much, in cents of euro, gross as declared (decision D4: no VAT split).
/// <paramref name="Quantity"/> and <paramref name="UnitAmountCents"/> are written by the offer on a request on measure (SP-10b-b), where
/// <paramref name="AmountCents"/> is always their product; a quote of SP-04b has neither (they are left out of the JSON when null).
/// </summary>
public sealed record ServiceRequestQuoteLine(string Label, int AmountCents, int? Quantity = null, int? UnitAmountCents = null);

/// <summary>The <c>kind</c> of a <see cref="ServiceRequestQuoteEvent"/>.</summary>
public static class ServiceRequestQuoteEventKinds
{
    /// <summary>The supplier sent a quote (or replaced the one that waited).</summary>
    public const string Sent = "sent";

    /// <summary>The customer accepted it: the request was taken at its total.</summary>
    public const string Accepted = "accepted";

    /// <summary>The customer turned it down: the request is "to answer" again.</summary>
    public const string Rejected = "rejected";

    /// <summary>The supplier took it back: the request is "to answer" again.</summary>
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// Something that happened to a quote of a request (<see cref="Entities.ServiceRequest.QuoteEventsJson"/>): the story the history of the
/// supplier's console tells once the offer itself is gone. A quote that lapsed is not here: the request's cancellation says it
/// (<see cref="ServiceRequestCancellationReasons.QuoteNotAnswered"/>).
/// </summary>
/// <param name="Kind">One of <see cref="ServiceRequestQuoteEventKinds"/>.</param>
/// <param name="At">UTC instant.</param>
/// <param name="TotalCents">The total of the quote the event is about.</param>
public sealed record ServiceRequestQuoteEvent(string Kind, DateTime At, int? TotalCents = null);

/// <summary>
/// The quote that waits for the customer's answer (SP-04b), as the readers give it: the lines and the total, the note, the other time
/// when the supplier offered one, and the deadline of the answer.
/// </summary>
/// <param name="SentAt">When it was sent: the version of the quote, which an answer repeats.</param>
/// <param name="ScheduledStartUtc">The other time the quote offers; <c>null</c> when it leans on the time of the request.</param>
/// <param name="AnswerBy">
/// Until when the customer of a showcase request has to answer (after it the request lapses); <c>null</c> for a host, who has no deadline.
/// </param>
public sealed record ServiceRequestQuote(
    DateTime SentAt,
    IReadOnlyList<ServiceRequestQuoteLine> Lines,
    int TotalCents,
    string? Note,
    DateTime? ScheduledStartUtc,
    DateTime? ScheduledEndUtc,
    DateTime? AnswerBy);

/// <summary>
/// The rules of a quote (SP-04b), as pure functions: what a valid quote is, which request it can be on, and when an answer is for the quote
/// that waits. The quote is an offer on a request still waiting for the supplier's answer (<see cref="ServiceRequestStatus.Richiesto"/>), like
/// the proposed time; it does not hold the slot.
/// </summary>
public static class ServiceRequestQuoteRules
{
    /// <summary>
    /// The lines of a quote, trimmed, and what they add up to. From one to <see cref="ServiceRequestLimits.MaxQuoteLines"/> lines, each with a
    /// description of at most <see cref="ServiceRequestLimits.QuoteLineLabelMaxLength"/> characters and an amount from 1 cent to the bound
    /// of a request; the total is at most that bound too.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="ServiceRequestErrorCodes.QuoteInvalid"/>.</exception>
    public static (IReadOnlyList<ServiceRequestQuoteLine> Lines, int TotalCents) Normalize(IReadOnlyList<ServiceRequestQuoteLine>? lines)
    {
        if (lines is null || lines.Count is 0 or > ServiceRequestLimits.MaxQuoteLines)
            throw Invalid();

        var normalized = new List<ServiceRequestQuoteLine>(lines.Count);
        long total = 0;
        foreach (var line in lines)
        {
            var label = line?.Label?.Trim();
            if (string.IsNullOrEmpty(label)
                || label.Length > ServiceRequestLimits.QuoteLineLabelMaxLength
                || label.Any(char.IsControl)
                || !ServiceRequestPricing.IsValidAmount(line!.AmountCents))
            {
                throw Invalid();
            }

            normalized.Add(new ServiceRequestQuoteLine(label, line.AmountCents));
            total += line.AmountCents;
        }

        if (!ServiceRequestPricing.IsValidAmount(total))
            throw Invalid();

        return (normalized, (int)total);
    }

    /// <summary>The note of a quote trimmed; <c>null</c> when it is blank. Longer than the column allows is not valid.</summary>
    /// <exception cref="DomainRuleException"><see cref="ServiceRequestErrorCodes.QuoteInvalid"/>.</exception>
    public static string? NormalizeNote(string? note)
    {
        var text = note?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length > ServiceRequestLimits.QuoteNoteMaxLength ? throw Invalid() : text;
    }

    /// <summary>True while the request has a quote waiting for the customer: new, and the supplier sent one.</summary>
    public static bool IsPending(ServiceRequestStatus status, DateTime? quoteSentAt) =>
        status == ServiceRequestStatus.Richiesto && quoteSentAt is not null;

    /// <summary>
    /// The quote that waits for the answer on <paramref name="request"/>, or <c>null</c> when there is none. The deadline is the customer's,
    /// so it is told only for a showcase request (a host has none, and the <c>ResponseDueAt</c> of its request is the supplier's).
    /// </summary>
    public static ServiceRequestQuote? PendingOf(Entities.ServiceRequest request) =>
        IsPending(request.Status, request.QuoteSentAt) && request is { QuoteSentAt: { } sentAt, QuoteTotalCents: { } total }
            ? new ServiceRequestQuote(
                sentAt,
                ServiceRequestJson.ReadQuoteLines(request.QuoteLinesJson),
                total,
                string.IsNullOrWhiteSpace(request.QuoteNote) ? null : request.QuoteNote,
                request.QuoteScheduledStartUtc,
                request.QuoteScheduledEndUtc,
                request.RentalContext == ServiceRequestRentalContext.Showcase ? request.ResponseDueAt : null)
            : null;

    /// <summary>
    /// True when <paramref name="asked"/> (what the customer read, repeated by its answer) is the version of the quote that waits:
    /// compared to the millisecond, so a client that reads the instant as a JavaScript date (which keeps milliseconds) and gives it back
    /// is still talking about the same quote. Two quotes of one request are never sent within the same millisecond: a person sends them.
    /// </summary>
    public static bool IsSameVersion(DateTime? stored, DateTime asked) =>
        stored is { } current
        && UtcDateTime.Normalize(current).Ticks / TimeSpan.TicksPerMillisecond == UtcDateTime.Normalize(asked).Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>The lines of a quote as the price lines of the request that took it (kind <see cref="ServiceRequestPriceLineKinds.Quote"/>).</summary>
    public static IReadOnlyList<ServiceRequestPriceLine> AsPriceLines(IReadOnlyList<ServiceRequestQuoteLine> lines) =>
        lines.Select(line => new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Quote, line.Label, line.AmountCents, line.Quantity, line.UnitAmountCents)).ToList();

    /// <summary>
    /// <paramref name="events"/> with <paramref name="added"/> at the end, keeping the last <see cref="ServiceRequestLimits.MaxQuoteEvents"/>:
    /// a request replaced by a supplier a hundred times keeps a row of a reasonable size.
    /// </summary>
    public static IReadOnlyList<ServiceRequestQuoteEvent> Append(IReadOnlyList<ServiceRequestQuoteEvent> events, ServiceRequestQuoteEvent added) =>
        events.Append(added).TakeLast(ServiceRequestLimits.MaxQuoteEvents).ToList();

    private static DomainRuleException Invalid() =>
        new(
            ServiceRequestErrorCodes.QuoteInvalid,
            ServiceRequestErrorCodes.QuoteInvalidMessageKey,
            ServiceRequestLimits.MaxQuoteLines,
            ServiceRequestLimits.QuoteLineLabelMaxLength,
            ServiceRequestLimits.MaxAmountCents / 100);
}

/// <summary>
/// Codes (ProblemDetails <c>code</c>) and message keys of what the customer of a supplier's public showcase is refused when it answers a
/// quote (SP-04b): the twins of the refusals about a proposed time (<see cref="ShowcaseBookingManagementErrors"/>). Snake case, never
/// renamed: the frontend branches on them. The other refusals are those of the area (<c>supplier_booking_not_found</c>, <c>supplier_booking_supplier_unavailable</c>,
/// 409 <c>service_request_quote_changed</c> and <c>service_request_state_changed</c>, 409 <c>supplier_slot_unavailable</c>).
/// </summary>
public static class ShowcaseBookingQuoteErrors
{
    /// <summary>422: the booking has no quote to answer (never sent, withdrawn, already answered, or the request moved on).</summary>
    public const string NoQuote = "supplier_booking_no_quote";

    /// <summary>422: the time to answer the quote has passed; the request is cancelled by the next run of the upkeep job.</summary>
    public const string QuoteExpired = "supplier_booking_quote_expired";

    public const string NoQuoteMessageKey = "SupplierBookingNoQuote";
    public const string QuoteExpiredMessageKey = "SupplierBookingQuoteExpired";

    /// <summary>Every <c>SharedResources</c> key the answers to a quote raise (a test checks that each one exists in Italian and English).</summary>
    public static IReadOnlyList<string> MessageKeys { get; } = [NoQuoteMessageKey, QuoteExpiredMessageKey];

    /// <summary>422: there is no quote to answer.</summary>
    public static DomainRuleException QuoteMissing() => new(NoQuote, NoQuoteMessageKey);

    /// <summary>422: the time to answer the quote has passed.</summary>
    public static DomainRuleException QuoteLapsed() => new(QuoteExpired, QuoteExpiredMessageKey);
}
