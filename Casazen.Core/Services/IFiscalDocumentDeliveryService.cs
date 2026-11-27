using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The document of a stay sent to the guest, and the public page its link opens (E12/F01/US02/T01, NF-83, ADR-022): an e-mail with a signed link, queued on Hangfire, and the summary and the
/// PDF that the link reads. CasaZen issues for every booking, so the guest of a portal gets the document too, on the relay address the portal gives them (it forwards to the guest).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only an issued guest document.</b> A draft, a discarded draft, an owner invoice and a document imported from the channel manager are never sent. An invoice, a receipt and a
/// credit note are, the Swiss ones like the Italian ones: nothing here depends on the country of the issuer.</item>
/// <item><b>The link carries the document and nothing else.</b> A time-limited ASP.NET Core Data Protection token (<see cref="FiscalDocumentLinkRules.TokenPurpose"/>,
/// <see cref="FiscalDocumentLinkRules.Lifetime"/>): no name, no address, no amount. Nothing is stored for it; a new e-mail makes a new token and the older ones stay valid until they expire.</item>
/// <item><b>The address.</b> The e-mail of the guest of the booking. Empty, malformed, the placeholder of an erased guest and the names that are never delivered or are kept for examples
/// (<c>.invalid</c>, <c>.test</c>, <c>.localhost</c>, <c>example.com</c>, <c>example.org</c>, <c>example.net</c>, with their subdomains) are refused, and so is a guest of the demo (a row of the showcase seed),
/// as a guest with no address: nothing is queued and nothing fails. A relay address of a portal is a normal address.</item>
/// <item><b>The public reads give nothing away.</b> Whatever makes a link not open (no token, a wrong one, an expired one, one made for another document, a document that does not exist or
/// is not an issued guest document of CasaZen) is the same answer: <c>null</c>, which the controller turns into the same 404. The token is never written to a log.</item>
/// <item><b>The tenant.</b> The send works through the tenant filter of its caller (the host's request, or the consumer of the event under the org of the event); the public reads have no
/// caller and the document is found by the signed id alone, whoever opens the link.</item>
/// </list>
/// </remarks>
public interface IFiscalDocumentDeliveryService
{
    /// <summary>
    /// The issued document sent to the guest of its booking by e-mail, when the host asks for it (<c>POST api/fiscal/documents/{id}/send-email</c>): a signed link that lives
    /// <see cref="FiscalDocumentLinkRules.Lifetime"/> in an e-mail queued on Hangfire, never sent inside the request. 404 <c>fiscal_document_not_found</c>; 422 <c>document_not_issued</c>
    /// (a draft or a discarded document), <c>document_owner_invoice</c> (an owner invoice goes to its owner), <c>document_read_only</c> (a document imported from the channel manager),
    /// <c>document_recipient_email_invalid</c> (no address, an erased guest, a placeholder, a reserved domain, a guest of the demo) and <c>document_email_not_sent</c> (the queue, or the public address of the web app, is not
    /// available: nothing was sent, and the answer never says it was).
    /// </summary>
    Task<FiscalDocumentDeliveryResult> SendToGuestAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// What the issuer asked for a document that was just issued (the consumer of <c>FiscalDocumentIssued</c> and <c>FiscalDocumentCreditNoteIssued</c>): the e-mail of
    /// <see cref="SendToGuestAsync"/> when the profile has «e-mail to the guest» on, nothing otherwise. A guest with no usable address, a document that is not a guest document that
    /// CasaZen issued and an environment with no e-mail provider or no public address of the web app are answers, not failures (<see cref="FiscalDocumentSendOutcome"/>): the document stays in
    /// CasaZen. A queue that refuses while both are configured is a failure of the send and is thrown as <see cref="SendToGuestAsync"/> does (422 <c>document_email_not_sent</c>), so the consumer
    /// lets the outbox deliver the event again. The event is delivered at least once, so a document whose e-mail an earlier delivery already queued is not queued again
    /// (<see cref="FiscalDocumentSendOutcome.AlreadyQueued"/>: remembered in memory for an hour, nothing is stored).
    /// </summary>
    Task<FiscalDocumentSendOutcome> SendAutomaticallyAsync(Guid documentId, CancellationToken ct = default);

    Task<FiscalDocumentLinkSummary?> GetSummaryByLinkAsync(Guid documentId, string? token, CancellationToken ct = default);

    Task<FiscalDocumentLinkFile?> GetPdfByLinkAsync(Guid documentId, string? token, string language, CancellationToken ct = default);
}

public sealed record FiscalDocumentDeliveryResult(Guid DocumentId, DateTime LinkExpiresAt);

public enum FiscalDocumentSendOutcome
{
    Queued = 1,

    NotAsked = 2,

    NoAddress = 3,

    NotSendable = 4,

    NotConfigured = 5,

    AlreadyQueued = 6,
}

public sealed record FiscalDocumentLinkSummary(Guid DocumentId, FiscalDocumentKind Kind, string Number, DateOnly IssueDate, decimal Total, string Currency);

public sealed record FiscalDocumentLinkFile(byte[] Content, string FileName);

public static class FiscalDocumentLinkRules
{
    public const string TokenPurpose = "Casazen.FiscalDocument.Link";

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
}

public static class FiscalDocumentDeliveryErrors
{
    public const string RecipientEmailInvalid = "document_recipient_email_invalid";

    public const string LinkInvalid = "document_link_invalid";

    public const string NotIssued = "document_not_issued";

    public const string EmailNotSent = "document_email_not_sent";
}
