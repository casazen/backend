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
    Task<FiscalDocument> CreateDraftAsync(Guid bookingId, string? userId, CancellationToken ct = default);

    Task<FiscalDocument> RefreshDraftAsync(Guid documentId, CancellationToken ct = default);

    Task DiscardDraftAsync(Guid documentId, CancellationToken ct = default);

    Task DiscardOwnerInvoiceDraftAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// The draft issued (see the remarks); 422 <c>document_confirmation_required</c> without <paramref name="confirm"/>, and 409 <c>document_draft_outdated</c>
    /// when the tax rule, the regime, the country or a treatment of the issuer changed since the draft was made (the amounts the host confirmed would not be
    /// the ones of the rule the document is issued with): <see cref="RefreshDraftAsync"/> first. 422 <c>document_owner_invoice</c> for an owner invoice: it is
    /// issued by <see cref="IOwnerInvoiceService.IssueAsync"/>, behind its own permission and flag.
    /// </summary>
    Task<FiscalDocument> IssueAsync(Guid documentId, bool confirm, CancellationToken ct = default);

    /// <summary>
    /// The issue of an owner invoice (E14/F01/US01/T01), called by <see cref="IOwnerInvoiceService.IssueAsync"/> once it checked the draft against its statement
    /// version: the same issue as a document of a guest (the gap-free number in the owner-invoice series of the profile, the snapshot of the issuer, the rule
    /// version, the Italian XML of an invoice), with <paramref name="vatToPost"/> posted to the owner ledger in the same transaction (nothing for 0). The tax rule
    /// is the one in force on the last day of the period of the invoice.
    /// </summary>
    Task<FiscalDocument> IssueOwnerInvoiceAsync(Guid documentId, decimal vatToPost, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The issue of a self-billed invoice, a TD17 (E17/F01/US03/T01), called by <see cref="ISelfBillingService.IssueAsync"/> once it checked the draft against the ledger: the
    /// same issue as a document of a guest (the gap-free number in the series <paramref name="seriesPattern"/> of the issuer the draft belongs to, the snapshot of that
    /// issuer, the rule version, the Italian XML, the state <c>to_send</c>), with <paramref name="rule"/> the one the self-billing service resolved from its settings (the VAT
    /// integrated at its rate, the type of document TD17). The XML is the invoice of the foreign supplier integrated by its recipient: the supplier is the cedente, the issuer is the
    /// cessionario that issues it (<c>SoggettoEmittente</c> <c>CC</c>). A draft that replaces a TD17 the SDI rejected (<see cref="IFiscalDocumentCorrectionService.StartSelfBillingCorrectionAsync"/>)
    /// holds its series and its number and is issued with them, and with the day of the TD17 it replaces, reserving no other number (the same issue as the replacement of an invoice to a guest).
    /// 404 <c>fiscal_document_not_found</c> (not a self-billed invoice); 422 <c>document_not_draft</c>, <c>issuer_profile_incomplete</c>, <c>series_too_long</c>, <c>document_replacement_invalid</c>.
    /// </summary>
    Task<FiscalDocument> IssueSelfBillingAsync(Guid documentId, FiscalTaxRule rule, string seriesPattern, string? userId, CancellationToken ct = default);

    /// <summary>
    /// The draft of a self-billed invoice discarded (E17/F01/US03/T01), called by <see cref="ISelfBillingService.DiscardDraftAsync"/> once it checked the organization: no number is
    /// consumed. 404 <c>fiscal_document_not_found</c> (not a self-billed invoice); 422 <c>document_not_draft</c>, <c>document_replacement_holds_number</c> (the draft replaces a TD17 the SDI rejected and
    /// holds its number: it is issued, never discarded, FIX/BE-TD17-CORRECTION; when the month no longer supports it, it leaves by <see cref="ISelfBillingService.CloseNotDueAsync"/>, which keeps the number
    /// reserved, FIX/BE-TD17-JOB-EXIT). The endpoints of the guest documents refuse it (422 <c>document_self_billing</c>).
    /// </summary>
    Task DiscardSelfBillingDraftAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// A credit note of an issued CasaZen document, issued at once in the same series: the whole document by default, never more than what is left
    /// to credit (422 <c>credit_amount_exceeds</c>). 422 <c>document_owner_invoice</c> for an owner invoice: its credit note gives its VAT back to the owner
    /// ledger, so it is made by <see cref="IOwnerInvoiceService.CreateCreditNoteAsync"/>, behind <c>agency.statement.manage</c> and its flag.
    /// </summary>
    Task<FiscalDocument> CreateCreditNoteAsync(Guid documentId, decimal? amount, string reason, bool confirm, string? userId, CancellationToken ct = default);

    /// <summary>
    /// A credit note of an issued owner invoice (E14/F01/US01/T01), called by <see cref="IOwnerInvoiceService.CreateCreditNoteAsync"/> once it checked the
    /// organization and the purpose of the document: the same credit note as <see cref="CreateCreditNoteAsync"/> (404 <c>fiscal_document_not_found</c> for a
    /// document that is not an owner invoice), which gives the VAT the invoice posted back to the owner ledger in the same transaction and generates again the
    /// draft or flagged statement version of the month.
    /// </summary>
    Task<FiscalDocument> CreateOwnerInvoiceCreditNoteAsync(Guid documentId, decimal? amount, string reason, bool confirm, string? userId, CancellationToken ct = default);

    Task<FiscalDocument?> GetAsync(Guid documentId, CancellationToken ct = default);

    Task<FiscalDocumentPage> ListAsync(HostScope scope, FiscalDocumentCriteria criteria, int page, int pageSize, CancellationToken ct = default);

    Task<IReadOnlyList<FiscalDocumentExportRow>> ListForExportAsync(HostScope scope, FiscalDocumentCriteria criteria, int limit, CancellationToken ct = default);

    Task<string> GetXmlAsync(Guid documentId, CancellationToken ct = default);

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

public enum EnsureDraftOutcome
{
    Created = 1,

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
    public bool Ran => NotRunReason is null;

    public static FiscalDocumentsDueResult NotRun(string reason) => new(reason, 0, 0, 0, 0);
}

public static class FiscalDocumentsDueSkipReasons
{
    public const string IssuanceOff = "issuance_off";

    public const string ProfileIncomplete = "profile_incomplete";

    public const string RunInProgress = "run_in_progress";
}

public static class FiscalDocumentBatch
{
    public const int MaxDocuments = 200;
}

/// <summary>
/// The selection of a bulk issue: the drafts named (<see cref="DocumentIds"/>) and, besides them, the drafts of the stays whose last day (the check-out) falls from
/// <see cref="From"/> to <see cref="To"/> (Europe/Rome days, both ends included) and of the properties <see cref="PropertyIds"/>. An empty list is no selection: with nothing else
/// it is refused, never read as «all».
/// </summary>
public sealed record FiscalDocumentBatchRequest(IReadOnlyList<Guid>? DocumentIds, DateOnly? From, DateOnly? To, IReadOnlyList<Guid>? PropertyIds);

public sealed record FiscalDocumentBatchRow(Guid Id, bool Issued, string? Number, string? ErrorCode);

public sealed record FiscalDocumentBatchResult(IReadOnlyList<FiscalDocumentBatchRow> Rows, int Remaining)
{
    public int Issued => Rows.Count(r => r.Issued);

    public int Failed => Rows.Count(r => !r.Issued);
}

public enum BookingChange
{
    Modified = 1,

    Cancelled = 2,

    NoShow = 3,

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
    public static BookingChangeOutcome None { get; } = new(0, 0, 0, 0, 0);

    public BookingChangeOutcome Plus(BookingChangeOutcome other) =>
        new(
            DraftsRefreshed + other.DraftsRefreshed,
            DraftsDiscarded + other.DraftsDiscarded,
            CreditNotesPrepared + other.CreditNotesPrepared,
            DocumentsIssued + other.DocumentsIssued,
            TodosRaised + other.TodosRaised);
}

public static class FiscalDocumentTodos
{
    public const string EntityType = "FiscalDocument";

    public const string BookingChangedReason = "fiscal_document_booking_changed";

    public const string CreditNoteToIssueReason = "fiscal_document_credit_note_to_issue";

    public const string OwnerEntityType = "PropertyOwner";

    public const string OwnerIssuerMissingReason = Casazen.Core.Models.BillingSetupItemKeys.OwnerIssuerMissing;
}

public sealed record FiscalDocumentCriteria(
    Guid? BookingId = null,
    Guid? PropertyId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    FiscalDocumentKind? Kind = null,
    FiscalDocumentStatus? Status = null,
    FiscalDocumentSource? Source = null);

public sealed record FiscalDocumentPage(IReadOnlyList<FiscalDocument> Items, int TotalCount, int Page, int PageSize)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

/// <summary>
/// One row of the list of the documents as a file has it (E06/F03/US03/T01): what the document stores (amounts as stored, in <see cref="Currency"/>,
/// the day of the issue a Europe/Rome calendar day) with the code of its booking and the name of its property when it has a booking, and the payment
/// state the current one. Only the name of the recipient is carried, never its fiscal code, VAT number or address. <paramref name="Purpose"/> tells what the document is for: the
/// counterparty of a self-billed invoice (TD17, E17/F01/US03) is the foreign supplier it integrates, not a customer, and the file names its kind for what it is.
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
    FiscalDocumentSource Source,
    FiscalDocumentPurpose Purpose = FiscalDocumentPurpose.GuestStay);

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

    public const string BatchSelectionRequired = "document_batch_selection_required";

    public const string BatchTooLarge = "document_batch_too_large";

    public const string BatchPeriodInvalid = "document_batch_period_invalid";

    public const string IssueFailed = "document_issue_failed";

    public const string OwnerIssuerMissing = Casazen.Core.Models.BillingSetupItemKeys.OwnerIssuerMissing;

    public const int CreditReasonMaxLength = 200;
}

public sealed record FiscalDocumentIssuedPayload(Guid DocumentId, Guid? BookingId, string Kind, Guid? CreditedDocumentId);
