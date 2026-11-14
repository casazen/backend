using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The fiscal documents CasaZen issues for the direct bookings of an organization whose issuer is CasaZen (FIS-03, PO-05, NF-83, ADR-019,
/// E06/F01/US01/T01), and the reading of every document of the organization, the ones imported from Octorate included.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only where CasaZen issues.</b> A draft is made only when <see cref="IDocumentIssuerService.ResolveIssuerAsync"/> says CasaZen (mode
/// <c>CasaZenDirectOnly</c>, a direct booking, <c>Features:FiscalDocumentIssuance</c> on, the booking not locked to Octorate), the profile of the issuer
/// and the rule of its regime are complete and the invoice data of the booking is. Never for an OTA booking, in any mode.</item>
/// <item><b>From the stored price.</b> Lodging (<c>BasePrice - CleaningFee</c>), cleaning (<c>CleaningFee</c>) and the tourist tax (a line without VAT with
/// the nature code of the rule), in the currency of the property; the amounts only through <see cref="FiscalDocumentTotals"/>.</item>
/// <item><b>Issue.</b> Confirmed by the host, in one READ COMMITTED transaction: the gap-free number, the validated FatturaPA XML (an invoice), the lock of
/// the booking to CasaZen, the state <c>to_send</c> (<c>not_applicable</c> for a receipt) and the event <c>FiscalDocumentIssued</c> recorded in the same
/// unit of work. An issued document is immutable: no method changes its amounts, recipient or lines.</item>
/// <item><b>Read-only imports.</b> Every change refuses a document imported from Octorate (422 <c>document_read_only</c>).</item>
/// <item><b>Tenant and scope.</b> The service works through the tenant filter of its caller; the list is narrowed in SQL to the scope it is given. The web
/// layer authorizes the booking of a single document.</item>
/// </list>
/// </remarks>
public interface IFiscalDocumentService
{
    /// <summary>The draft of the booking: made from its stored price, or the draft it already has, refreshed.</summary>
    Task<FiscalDocument> CreateDraftAsync(Guid bookingId, string? userId, CancellationToken ct = default);

    /// <summary>The draft made again from the current price, recipient and rule of its booking.</summary>
    Task<FiscalDocument> RefreshDraftAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>The draft discarded: no number is consumed.</summary>
    Task DiscardDraftAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// The draft issued (see the remarks); 422 <c>document_confirmation_required</c> without <paramref name="confirm"/>, and 409 <c>document_draft_outdated</c>
    /// when the tax rule, the regime, the country or a treatment of the issuer changed since the draft was made (the amounts the host confirmed would not be
    /// the ones of the rule the document is issued with): <see cref="RefreshDraftAsync"/> first.
    /// </summary>
    Task<FiscalDocument> IssueAsync(Guid documentId, bool confirm, CancellationToken ct = default);

    /// <summary>
    /// A credit note of an issued CasaZen document, issued at once in the same series: the whole document by default, never more than what is left
    /// to credit (422 <c>credit_amount_exceeds</c>).
    /// </summary>
    Task<FiscalDocument> CreateCreditNoteAsync(Guid documentId, decimal? amount, string reason, bool confirm, string? userId, CancellationToken ct = default);

    /// <summary>One document with its lines, or <c>null</c> when it is not visible to the caller.</summary>
    Task<FiscalDocument?> GetAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>One page of the documents of the scope (newest first), narrowed in SQL to the properties the scope reaches.</summary>
    Task<FiscalDocumentPage> ListAsync(HostScope scope, FiscalDocumentCriteria criteria, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// The documents of the scope for a file (FIS-07, NF-87, E06/F03/US03/T01), newest first and at most <paramref name="limit"/> of them: the same
    /// narrowing in SQL as <see cref="ListAsync"/> (the scope, then <paramref name="criteria"/>), the payment state the current one, and only the
    /// documents that were issued, whatever <see cref="FiscalDocumentCriteria.Status"/> says: a draft has no number and no date, and a discarded one
    /// is not a document. The XML and the lines are not read.
    /// </summary>
    Task<IReadOnlyList<FiscalDocumentExportRow>> ListForExportAsync(HostScope scope, FiscalDocumentCriteria criteria, int limit, CancellationToken ct = default);

    /// <summary>The FatturaPA XML of an issued CasaZen invoice or credit note.</summary>
    Task<string> GetXmlAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>The PDF of a CasaZen document in <paramref name="language"/>.</summary>
    Task<byte[]> GetPdfAsync(Guid documentId, string language, CancellationToken ct = default);

    /// <summary>
    /// What the documents of a booking need after the booking changed (E12/F01/US01/T01): the draft is made again from the booking (or discarded when the booking is
    /// no longer active and nothing was collected), and an issued document is <b>never</b> changed: the host is told with a «To do»
    /// (<see cref="FiscalDocumentTodos.BookingChangedReason"/>, no personal data) that a credit note may be needed. Idempotent: a change delivered again raises no second item.
    /// A booking that is not found, and one with no document, change nothing.
    /// </summary>
    Task<BookingChangeOutcome> ApplyBookingChangeAsync(Guid bookingId, BookingChange change, CancellationToken ct = default);

    /// <summary>
    /// The draft of a stay that ended (E12/F01/US01/T02): made when the booking has no live document (not discarded, not a credit note, not credited in full), left alone
    /// otherwise, whoever made it (the check-out event, the daily run, the host): one draft for a booking however many times and from wherever it is asked. The check and the
    /// draft are taken under the lock of the booking. The refusals of <see cref="CreateDraftAsync"/> (a booking that is not active, the data of the guest or of the issuer
    /// missing, no tax rule, a flag that is off) are raised as they are.
    /// </summary>
    Task<EnsureDraftOutcome> EnsureDraftAsync(Guid bookingId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The organizations the daily run looks at: the ones that have an issuer profile, whatever the tenant of the caller (the run has none). The run enters each of them in a
    /// scope of its own before <see cref="ProcessDueDocumentsAsync"/>.
    /// </summary>
    Task<IReadOnlyList<Guid>> OrgsWithDocumentsDueAsync(CancellationToken ct = default);

    /// <summary>
    /// The daily run of one organization, under its tenant (E12/F01/US01/T02): each active booking whose check-out day is before today (Europe/Rome) and not older than
    /// <paramref name="lookbackDays"/> days gets its draft when it has no live document; with <c>AutoIssueGuestDocuments</c> on the draft is issued through the same path as the
    /// API (without it the drafts wait for the host). At most <paramref name="limit"/> bookings, the most recent first, the rest at the next run; idempotent. Nothing runs while
    /// <c>Features:FiscalDocumentIssuance</c> is off, while the profile misses an item the documents need, or while another run of the same organization holds the lock.
    /// A booking that cannot get its document now (data missing, rule not configured) is skipped and read again at the next run. A draft made earlier is made again right before the
    /// automatic issue, so what is issued is what the booking bills now. The issued document of a stay of a portal that ended and then changed on the portal (the import records no event
    /// for a stay that is over) is compared with the lines the booking bills now: the host gets the «To do» <see cref="FiscalDocumentTodos.BookingChangedReason"/>, once for the document.
    /// </summary>
    Task<FiscalDocumentsDueResult> ProcessDueDocumentsAsync(Guid orgId, int limit, int lookbackDays, CancellationToken ct = default);

    /// <summary>
    /// The bulk issue (E12/F01/US01/T02): the drafts named by <see cref="FiscalDocumentBatchRequest.DocumentIds"/> and the drafts of the stays of the period and properties of the
    /// request, at most <see cref="FiscalDocumentBatch.MaxDocuments"/>, each through the issue path of the API in its own transaction. 422 <c>document_batch_selection_required</c>
    /// for an empty selection (never «all»), <c>document_batch_too_large</c> for more than the maximum ids, <c>document_batch_period_invalid</c> for a period that ends before it
    /// starts. The answer has one row per document and never says that all were issued when one failed. A caller restricted to some properties issues only the documents of the
    /// properties it reaches: any other id is a row <c>fiscal_document_not_found</c>, the same answer as an id that does not exist.
    /// </summary>
    Task<FiscalDocumentBatchResult> IssueBatchAsync(HostScope scope, FiscalDocumentBatchRequest request, CancellationToken ct = default);
}

/// <summary>What <see cref="IFiscalDocumentService.EnsureDraftAsync"/> found.</summary>
public enum EnsureDraftOutcome
{
    /// <summary>The booking had no live document: its draft was made.</summary>
    Created = 1,

    /// <summary>The booking has a live document already (a draft, an issued one, one issued elsewhere): nothing was made.</summary>
    AlreadyHasDocument = 2,
}

/// <summary>What the daily run of one organization did: counts only, nothing personal.</summary>
/// <param name="NotRunReason">One of <see cref="FiscalDocumentsDueSkipReasons"/> when the run did nothing at all; <c>null</c> when it ran.</param>
/// <param name="Examined">The bookings looked at (at most the limit).</param>
/// <param name="DraftsCreated">The drafts made.</param>
/// <param name="DocumentsIssued">The documents issued (the automatic issue).</param>
/// <param name="Skipped">The bookings that could not get their document now (a refusal of the rules): they are read again at the next run.</param>
/// <param name="ChangesTold">The issued documents of the stays of a portal that ended and changed on the portal afterwards, for which the host got the «To do» of a change.</param>
public sealed record FiscalDocumentsDueResult(string? NotRunReason, int Examined, int DraftsCreated, int DocumentsIssued, int Skipped, int ChangesTold = 0)
{
    /// <summary>True when the run looked at the organization.</summary>
    public bool Ran => NotRunReason is null;

    /// <summary>A run that did nothing, with its reason.</summary>
    public static FiscalDocumentsDueResult NotRun(string reason) => new(reason, 0, 0, 0, 0);
}

/// <summary>Why a daily run did nothing (<see cref="FiscalDocumentsDueResult.NotRunReason"/>).</summary>
public static class FiscalDocumentsDueSkipReasons
{
    /// <summary><c>Features:FiscalDocumentIssuance</c> is off.</summary>
    public const string IssuanceOff = "issuance_off";

    /// <summary>The profile of the issuer misses an item the documents need (the setup of the issuer is not complete).</summary>
    public const string ProfileIncomplete = "profile_incomplete";

    /// <summary>Another run of the same organization is working.</summary>
    public const string RunInProgress = "run_in_progress";
}

/// <summary>The limits of the bulk issue.</summary>
public static class FiscalDocumentBatch
{
    /// <summary>The most documents one request issues.</summary>
    public const int MaxDocuments = 200;
}

/// <summary>
/// The selection of a bulk issue: the drafts named (<see cref="DocumentIds"/>) and, besides them, the drafts of the stays whose last day (the check-out) falls from
/// <see cref="From"/> to <see cref="To"/> (Europe/Rome days, both ends included) and of the properties <see cref="PropertyIds"/>. An empty list is no selection: with nothing else
/// it is refused, never read as «all».
/// </summary>
public sealed record FiscalDocumentBatchRequest(IReadOnlyList<Guid>? DocumentIds, DateOnly? From, DateOnly? To, IReadOnlyList<Guid>? PropertyIds);

/// <summary>The outcome of one document of a bulk issue: issued with its number, or not with the stable code of why.</summary>
public sealed record FiscalDocumentBatchRow(Guid Id, bool Issued, string? Number, string? ErrorCode);

/// <summary>The answer of a bulk issue: one row per document, and how many more documents the selection matched beyond the maximum.</summary>
public sealed record FiscalDocumentBatchResult(IReadOnlyList<FiscalDocumentBatchRow> Rows, int Remaining)
{
    /// <summary>The documents issued.</summary>
    public int Issued => Rows.Count(r => r.Issued);

    /// <summary>The documents that were not issued.</summary>
    public int Failed => Rows.Count(r => !r.Issued);
}

/// <summary>What happened to a booking that its documents have to follow (<see cref="IFiscalDocumentService.ApplyBookingChangeAsync"/>).</summary>
public enum BookingChange
{
    /// <summary>The dates, the guests, the amounts or the property changed.</summary>
    Modified = 1,

    /// <summary>The booking was cancelled.</summary>
    Cancelled = 2,

    /// <summary>The host marked the stay as a no-show.</summary>
    NoShow = 3,

    /// <summary>A refund of a payment of the booking succeeded.</summary>
    Refunded = 4,
}

/// <summary>What <see cref="IFiscalDocumentService.ApplyBookingChangeAsync"/> did: counts only, nothing personal.</summary>
/// <param name="DraftsRefreshed">Drafts made again from the changed booking.</param>
/// <param name="DraftsDiscarded">Drafts of a booking that bills nothing any more, discarded.</param>
/// <param name="CreditNotesPrepared">Credit notes (or reversal receipts) made as drafts for the issued documents the change leaves wrong.</param>
/// <param name="DocumentsIssued">Documents issued at once because the profile issues without a person (the corrections of an automatic issue).</param>
/// <param name="TodosRaised">«To do» items raised for the host (an item already open counts as raised).</param>
public sealed record BookingChangeOutcome(int DraftsRefreshed, int DraftsDiscarded, int CreditNotesPrepared, int DocumentsIssued, int TodosRaised)
{
    /// <summary>Nothing was done.</summary>
    public static BookingChangeOutcome None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>The outcomes of two steps added up.</summary>
    public BookingChangeOutcome Plus(BookingChangeOutcome other) =>
        new(
            DraftsRefreshed + other.DraftsRefreshed,
            DraftsDiscarded + other.DraftsDiscarded,
            CreditNotesPrepared + other.CreditNotesPrepared,
            DocumentsIssued + other.DocumentsIssued,
            TodosRaised + other.TodosRaised);
}

/// <summary>The «To do» items of the fiscal documents the service raises (no personal data: a code, the document and its property).</summary>
public static class FiscalDocumentTodos
{
    /// <summary>The kind of entity of the item: the document (the frontend opens it from the id).</summary>
    public const string EntityType = "FiscalDocument";

    /// <summary>A booking changed after its document was issued: a credit note may be needed. The document is never rewritten.</summary>
    public const string BookingChangedReason = "fiscal_document_booking_changed";

    /// <summary>«Controlla ed emetti la nota di credito»: the credit note (or reversal receipt) of a correction is waiting as a draft for the host to check and issue.</summary>
    public const string CreditNoteToIssueReason = "fiscal_document_credit_note_to_issue";
}

/// <summary>The filters of the list of the documents; the days are Europe/Rome calendar days of the issue (of the creation for a draft).</summary>
public sealed record FiscalDocumentCriteria(
    Guid? BookingId = null,
    Guid? PropertyId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    FiscalDocumentKind? Kind = null,
    FiscalDocumentStatus? Status = null,
    FiscalDocumentSource? Source = null);

/// <summary>One page of documents.</summary>
public sealed record FiscalDocumentPage(IReadOnlyList<FiscalDocument> Items, int TotalCount, int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

/// <summary>
/// One row of the list of the documents as a file has it (E06/F03/US03/T01): what the document stores (amounts as stored, in <see cref="Currency"/>,
/// the day of the issue a Europe/Rome calendar day) with the code of its booking and the name of its property when it has a booking, and the payment
/// state the current one. Only the name of the recipient is carried, never its fiscal code, VAT number or address.
/// </summary>
public sealed record FiscalDocumentExportRow(
    Guid Id,
    FiscalDocumentKind Kind,
    string? Number,
    DateOnly? IssueDate,
    string? BookingCode,
    string? PropertyName,
    string? RecipientName,
    decimal TaxableBase,
    decimal VatAmount,
    decimal StampDuty,
    decimal Total,
    string Currency,
    FiscalDocumentPaymentState PaymentState,
    string SdiStatus,
    FiscalDocumentSource Source);

/// <summary>The stable codes of the errors of the fiscal documents, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class FiscalDocumentErrors
{
    public const string BookingNotDirect = DocumentIssuerErrorCodes.BookingNotDirect;
    public const string IssuerConflict = DocumentIssuerErrorCodes.IssuerConflict;
    public const string RuleNotConfigured = DocumentIssuerErrorCodes.RuleNotConfigured;
    public const string ProfileIncomplete = DocumentIssuerErrorCodes.ProfileIncomplete;
    public const string IssuanceNotAvailable = DocumentIssuerErrorCodes.IssuanceNotAvailable;
    public const string InvoiceDataMissing = "document_invoice_data_missing";
    public const string BookingNotActive = "booking_not_active";
    public const string AlreadyIssued = "document_already_issued";
    public const string NotDraft = "document_not_draft";

    /// <summary>409: the tax rule (or the regime, the country, a treatment of the issuer) changed since the draft was made: refresh it before issuing.</summary>
    public const string DraftOutdated = "document_draft_outdated";

    public const string CreditAmountExceeds = "credit_amount_exceeds";
    public const string ConfirmationRequired = "document_confirmation_required";
    public const string ReadOnly = "document_read_only";
    public const string NotCreditable = "document_not_creditable";
    public const string NotFound = "fiscal_document_not_found";
    public const string XmlUnavailable = "fiscal_document_xml_unavailable";

    /// <summary>422: a bulk issue with an empty selection (no document, no period, no property): never read as «all».</summary>
    public const string BatchSelectionRequired = "document_batch_selection_required";

    /// <summary>422: more documents than <see cref="FiscalDocumentBatch.MaxDocuments"/> in one bulk issue.</summary>
    public const string BatchTooLarge = "document_batch_too_large";

    /// <summary>422: the period of a bulk issue ends before it starts.</summary>
    public const string BatchPeriodInvalid = "document_batch_period_invalid";

    /// <summary>The code of a row of a bulk issue whose document failed for a reason that is not a rule (the database): the document stays a draft and can be issued again.</summary>
    public const string IssueFailed = "document_issue_failed";

    /// <summary>The longest reason of a credit note.</summary>
    public const int CreditReasonMaxLength = 200;
}

/// <summary>The payload of <c>FiscalDocumentIssued</c> and <c>FiscalDocumentCreditNoteIssued</c>: ids and codes only.</summary>
public sealed record FiscalDocumentIssuedPayload(Guid DocumentId, Guid? BookingId, string Kind, Guid? CreditedDocumentId);
