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

public static class ServiceRequestQuoteEventKinds
{
    public const string Sent = "sent";

    public const string Accepted = "accepted";

    public const string Rejected = "rejected";

    public const string Withdrawn = "withdrawn";
}

public sealed record ServiceRequestQuoteEvent(string Kind, DateTime At, int? TotalCents = null);

public sealed record ServiceRequestQuote(
    DateTime SentAt,
    IReadOnlyList<ServiceRequestQuoteLine> Lines,
    int TotalCents,
    string? Note,
    DateTime? ScheduledStartUtc,
    DateTime? ScheduledEndUtc,
    DateTime? AnswerBy);

public static class ServiceRequestQuoteRules
{
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

    public static string? NormalizeNote(string? note)
    {
        var text = note?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;

        return text.Length > ServiceRequestLimits.QuoteNoteMaxLength ? throw Invalid() : text;
    }

    public static bool IsPending(ServiceRequestStatus status, DateTime? quoteSentAt) =>
        status == ServiceRequestStatus.Richiesto && quoteSentAt is not null;

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

    public static IReadOnlyList<ServiceRequestPriceLine> AsPriceLines(IReadOnlyList<ServiceRequestQuoteLine> lines) =>
        lines.Select(line => new ServiceRequestPriceLine(ServiceRequestPriceLineKinds.Quote, line.Label, line.AmountCents, line.Quantity, line.UnitAmountCents)).ToList();

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
    public const string NoQuote = "supplier_booking_no_quote";

    public const string QuoteExpired = "supplier_booking_quote_expired";

    public const string NoQuoteMessageKey = "SupplierBookingNoQuote";
    public const string QuoteExpiredMessageKey = "SupplierBookingQuoteExpired";

    public static IReadOnlyList<string> MessageKeys { get; } = [NoQuoteMessageKey, QuoteExpiredMessageKey];

    public static DomainRuleException QuoteMissing() => new(NoQuote, NoQuoteMessageKey);

    public static DomainRuleException QuoteLapsed() => new(QuoteExpired, QuoteExpiredMessageKey);
}
