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
/// <item><b>The address.</b> The e-mail of the guest of the booking. Empty, malformed, the placeholder of an erased guest and the names that are never delivered are refused; a relay address
/// of a portal is a normal address.</item>
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
    /// <c>document_recipient_email_invalid</c> (no address, an erased guest, a placeholder) and <c>document_email_not_sent</c> (the queue, or the public address of the web app, is not
    /// available: nothing was sent, and the answer never says it was).
    /// </summary>
    Task<FiscalDocumentDeliveryResult> SendToGuestAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// What the issuer asked for a document that was just issued (the consumer of <c>FiscalDocumentIssued</c> and <c>FiscalDocumentCreditNoteIssued</c>): the e-mail of
    /// <see cref="SendToGuestAsync"/> when the profile has «e-mail to the guest» on, nothing otherwise. A guest with no usable address, a document that is not a guest document that
    /// CasaZen issued and an environment with no e-mail provider or no public address of the web app are answers, not failures (<see cref="FiscalDocumentSendOutcome"/>): the document stays in
    /// CasaZen. A queue that refuses while both are configured is a failure of the send and is thrown as <see cref="SendToGuestAsync"/> does (422 <c>document_email_not_sent</c>), so the consumer
    /// lets the outbox deliver the event again.
    /// </summary>
    Task<FiscalDocumentSendOutcome> SendAutomaticallyAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// The summary the public page shows (<c>POST api/public/fiscal-documents/{id}</c>): kind, number, day, total and currency of the document that <paramref name="token"/> was made for;
    /// <c>null</c> for anything else, with no word of why (see the remarks). The token is never logged.
    /// </summary>
    Task<FiscalDocumentLinkSummary?> GetSummaryByLinkAsync(Guid documentId, string? token, CancellationToken ct = default);

    /// <summary>
    /// The PDF of the document that <paramref name="token"/> was made for, in <paramref name="language"/> (<c>POST api/public/fiscal-documents/{id}/pdf</c>): the one the host sees, drawn by
    /// <see cref="IFiscalDocumentService.GetPdfAsync"/> under the tenant of the document. <c>null</c> for anything that is not a link that opens (see the remarks).
    /// </summary>
    Task<FiscalDocumentLinkFile?> GetPdfByLinkAsync(Guid documentId, string? token, string language, CancellationToken ct = default);
}

/// <summary>What <see cref="IFiscalDocumentDeliveryService.SendToGuestAsync"/> did: the e-mail is queued, and the link in it stops opening at <see cref="LinkExpiresAt"/> (UTC).</summary>
public sealed record FiscalDocumentDeliveryResult(Guid DocumentId, DateTime LinkExpiresAt);

/// <summary>What <see cref="IFiscalDocumentDeliveryService.SendAutomaticallyAsync"/> found: counts and codes only, nothing personal.</summary>
public enum FiscalDocumentSendOutcome
{
    /// <summary>The issuer asked for it and the e-mail with the link is queued.</summary>
    Queued = 1,

    /// <summary>The issuer did not ask for it («e-mail to the guest» is off): the document waits for the host to send it, if the host wants to.</summary>
    NotAsked = 2,

    /// <summary>The guest has no address to send to (empty, malformed, erased, a placeholder): the document stays in CasaZen and in the package of the accountant.</summary>
    NoAddress = 3,

    /// <summary>Not an issued guest document that CasaZen issued (an owner invoice, a draft, an imported document): there is no guest to send it to.</summary>
    NotSendable = 4,

    /// <summary>
    /// The e-mail provider or the public address of the web app is not configured: nothing can be sent from this environment, so there is nothing to try again (the host's send answers 422
    /// <c>document_email_not_sent</c>). Outside Development and Testing the startup refuses such a configuration, so this is the state of a development machine.
    /// </summary>
    NotConfigured = 5,
}

/// <summary>What the public page shows of the document its link opens: the kind, the number, the day of the issue, the total and its currency, and no more.</summary>
public sealed record FiscalDocumentLinkSummary(Guid DocumentId, FiscalDocumentKind Kind, string Number, DateOnly IssueDate, decimal Total, string Currency);

/// <summary>The PDF of the document its link opens, with the name of the file.</summary>
public sealed record FiscalDocumentLinkFile(byte[] Content, string FileName);

/// <summary>The signed link of a document sent to the guest (E12/F01/US02/T01).</summary>
public static class FiscalDocumentLinkRules
{
    /// <summary>The Data Protection purpose of the token: a token made for another purpose does not open here, and the other way round.</summary>
    public const string TokenPurpose = "Casazen.FiscalDocument.Link";

    /// <summary>How long a link opens: 30 days from the moment the e-mail is queued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);
}

/// <summary>The stable codes of the errors of the delivery of a document, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class FiscalDocumentDeliveryErrors
{
    /// <summary>422: the guest has no address the document can be sent to (empty, malformed, an erased guest, a placeholder).</summary>
    public const string RecipientEmailInvalid = "document_recipient_email_invalid";

    /// <summary>404: the link of the public page does not open: the same answer for every reason (a wrong, expired or foreign token, a document that is not there).</summary>
    public const string LinkInvalid = "document_link_invalid";

    /// <summary>422: only an issued document is sent; a draft or a discarded draft is not a document yet.</summary>
    public const string NotIssued = "document_not_issued";

    /// <summary>422: the e-mail could not be queued (no e-mail provider, no public address of the web app, the queue is down): nothing was sent.</summary>
    public const string EmailNotSent = "document_email_not_sent";
}
