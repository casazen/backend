using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Fiscal;

/// <summary>
/// <c>POST api/fiscal/documents/{id}/send-email</c> (E12/F01/US02/T01): the e-mail with the link was queued, never that it was delivered (the queue delivers it, and retries). <see cref="LinkExpiresAt"/>
/// is the last instant (UTC) the link in it opens; no address and no token are in the answer.
/// </summary>
public sealed record FiscalDocumentEmailQueuedDto(Guid DocumentId, bool Queued, DateTime LinkExpiresAt)
{
    public static FiscalDocumentEmailQueuedDto From(FiscalDocumentDeliveryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new FiscalDocumentEmailQueuedDto(result.DocumentId, Queued: true, result.LinkExpiresAt);
    }
}

/// <summary>
/// The body of <c>POST api/public/fiscal-documents/{id}</c> and <c>…/pdf</c> (E12/F01/US02/T01): the token of the link, in the body and never in the address, so no access log holds it. A missing token
/// is a link that does not open, like a wrong one (404 <c>document_link_invalid</c>), so the field has no validation of its own: nothing answers differently for a token of the wrong shape.
/// </summary>
public sealed record PublicFiscalDocumentLinkRequest
{
    public string? Token { get; init; }
}

/// <summary>
/// What the public page shows of the document its link opens: the kind (<c>Invoice</c>, <c>Receipt</c> or <c>CreditNote</c>), the number, the day of the issue, the total and its currency, and nothing
/// of the recipient, the issuer, the lines or the booking.
/// </summary>
public sealed record PublicFiscalDocumentSummaryDto(Guid Id, string Kind, string Number, DateOnly IssueDate, decimal Total, string Currency)
{
    public static PublicFiscalDocumentSummaryDto From(FiscalDocumentLinkSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new PublicFiscalDocumentSummaryDto(summary.DocumentId, summary.Kind.ToString(), summary.Number, summary.IssueDate, summary.Total, summary.Currency);
    }
}
