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

public sealed record PublicFiscalDocumentLinkRequest
{
    public string? Token { get; init; }
}

public sealed record PublicFiscalDocumentSummaryDto(Guid Id, string Kind, string Number, DateOnly IssueDate, decimal Total, string Currency)
{
    public static PublicFiscalDocumentSummaryDto From(FiscalDocumentLinkSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new PublicFiscalDocumentSummaryDto(summary.DocumentId, summary.Kind.ToString(), summary.Number, summary.IssueDate, summary.Total, summary.Currency);
    }
}
