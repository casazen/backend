using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The Italian electronic invoices of CasaZen on their way to the SDI (E17/F01/US02/T01, NF-116, NF-86, ADR-019, ADR-022): their submission through the accredited channel,
/// what the SDI answers (delivered, rejected), and the declaration of an invoice the issuer sent by hand. The port serves every issuer, the organization and the owners
/// who issue.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Nothing is ever «sent» by itself.</b> A document is <c>submitted</c> only with the answer of the provider that accepted it, <c>delivered</c> or <c>rejected</c> only with
/// an outcome (the provider's notification, the poll of the job, a declaration), <c>manual_declared</c> only with a declaration of a person. With no provider configured a
/// submission answers <see cref="SdiSubmitOutcome.NotConfigured"/> and changes nothing; the declaration works all the same.</item>
/// <item><b>One entry for the outcomes.</b> <see cref="RecordOutcomeAsync"/> is the way in of the provider's webhook, of the poll job and of the declaration of a rejection;
/// it is idempotent, and a later «submitted» never overrides «delivered» or «rejected». A delivered invoice is never rejected afterwards (a rejected one was never delivered:
/// the rule of the reuse of its number, E17/F01/US02/T03).</item>
/// <item><b>The events.</b> <c>FiscalDocumentSdiDelivered</c> and <c>FiscalDocumentSdiRejected</c> are recorded in the unit of work of the outcome, once for a document.</item>
/// <item><b>Tenant.</b> The calls of a host work through the tenant filter of their caller; the two reads of the job and of the webhook (<see cref="SubmittedDocumentsAsync"/>,
/// <see cref="FindByTransmissionAsync"/>) are system reads across the organizations that name the org in their answer, so the caller enters it before it writes.</item>
/// </list>
/// </remarks>
public interface IFiscalDocumentSdiService
{
    /// <summary>The SDI state of a document of the org (what a host reads); <c>null</c> when the document is not found.</summary>
    Task<FiscalDocumentSdiState?> GetStateAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Submits an issued CasaZen document in state <c>to_send</c> (a FatturaPA invoice or credit note) to the SDI through the provider. With no provider configured: <see cref="SdiSubmitOutcome.NotConfigured"/>,
    /// nothing changes. The provider accepted: <c>submitted</c> and its id. The provider refused: the document stays <c>to_send</c> with the code of the refusal. Already
    /// <c>submitted</c>: <see cref="SdiSubmitOutcome.AlreadySubmitted"/>, nothing is sent twice. A transport failure is thrown, the document unchanged (it stays <c>to_send</c>).
    /// 404 <c>fiscal_document_not_found</c>; 422 <c>document_read_only</c> (an imported document), <c>sdi_not_electronic</c> (a receipt, a Swiss document, an owner invoice is one, a draft is not),
    /// 409 <c>sdi_not_to_send</c> (delivered, rejected, declared).
    /// </summary>
    Task<SdiSubmitResult> SubmitAsync(Guid documentId, CancellationToken ct = default);

    /// <summary>
    /// Records what the SDI said about a document (the single entry of the provider's answers, of the poll job and of the declarations). <c>delivered</c> or <c>rejected</c> (with its
    /// code and text) and the day of the state; idempotent; a <see cref="SdiOutcomeKind.Submitted"/> outcome changes nothing; a rejection of a delivered document and a delivery of a
    /// rejected one are <see cref="SdiRecordResult.Ignored"/> (logged, never applied). 404, 422 <c>document_read_only</c> as for the submission.
    /// </summary>
    Task<SdiRecordResult> RecordOutcomeAsync(Guid documentId, SdiOutcome outcome, CancellationToken ct = default);

    /// <summary>
    /// The issuer sent the file through its own channel (the portal of the Agenzia delle Entrate): the document becomes <c>manual_declared</c> with the day and the reference of the
    /// sending (both required, the day not in the future and not before the issue), or, for a rejection (the code and the text of the SDI notification), the outcome is recorded as
    /// <c>rejected</c>. Works with no provider configured. 422 <c>sdi_declaration_invalid</c> (with the <c>fields</c>), 409 <c>sdi_state_final</c> (delivered, rejected) or
    /// <c>sdi_not_to_send</c> (submitted through the provider: the provider's answer decides).
    /// </summary>
    Task<FiscalDocumentSdiState> DeclareManuallyAsync(Guid documentId, SdiDeclaration declaration, CancellationToken ct = default);

    /// <summary>
    /// The documents <c>submitted</c> through the provider, of every organization, the oldest state first, at most <paramref name="limit"/> (a system read, for the job
    /// <c>fiscal-sdi-status</c>): the org, the document and the provider's id of the submission, nothing else.
    /// </summary>
    Task<IReadOnlyList<SdiSubmittedDocument>> SubmittedDocumentsAsync(int limit, CancellationToken ct = default);

    /// <summary>The document a provider's id of a submission is about (a system read, for the webhook); <c>null</c> when no document of CasaZen has it.</summary>
    Task<SdiSubmittedDocument?> FindByTransmissionAsync(string transmissionId, CancellationToken ct = default);

    /// <summary>
    /// The issuers of the org that send electronic invoices to the SDI and where each stands with the channel (E17/F01/US02/T02): the profile of the organization with a VAT
    /// number and the profiles of the owners who have one, each <c>pending</c> (the provider refused the submission of one of its documents because its VAT number is not enabled
    /// yet, and no later submission was accepted), <c>registered</c> (the provider accepted a submission of it) or <c>unknown</c> (nothing was submitted yet: it is enabled at the
    /// first submission). Derived from the documents: it asks the provider nothing. The org is the scope's (so the caller may be a job without a tenant). <b>A caller who reaches only some
    /// properties</b> («Solo alcuni», an owner) sees only the issuers that have a document on a booking of those properties, as the list of the documents does: never the issuers of the whole org
    /// (an org-wide scope sees them all).
    /// </summary>
    Task<IReadOnlyList<SdiIssuerRegistration>> IssuerRegistrationsAsync(HostScope scope, CancellationToken ct = default);
}

/// <summary>What <see cref="IFiscalDocumentSdiService.SubmitAsync"/> did.</summary>
public enum SdiSubmitOutcome
{
    /// <summary>No provider is configured: nothing was sent and nothing changed (the file can be uploaded by hand and the document declared).</summary>
    NotConfigured = 1,

    /// <summary>The provider accepted the file: the document is <c>submitted</c>.</summary>
    Submitted = 2,

    /// <summary>The provider refused the file: the document stays <c>to_send</c> with the code of the refusal.</summary>
    Refused = 3,

    /// <summary>The document was submitted already (a second request, or a double click): nothing was sent twice.</summary>
    AlreadySubmitted = 4,
}

/// <summary>The answer of a submission: what happened, the state the document is in now and the code of a refusal.</summary>
/// <param name="Outcome">What the submission did.</param>
/// <param name="Status">The <see cref="FiscalDocument.SdiStatus"/> of the document after it.</param>
/// <param name="ErrorCode">The code of the refusal (<see cref="SdiSubmissionErrors"/>) for <see cref="SdiSubmitOutcome.Refused"/>; <c>null</c> otherwise.</param>
public sealed record SdiSubmitResult(SdiSubmitOutcome Outcome, string Status, string? ErrorCode = null);

/// <summary>What <see cref="IFiscalDocumentSdiService.RecordOutcomeAsync"/> did with an outcome.</summary>
public enum SdiRecordResult
{
    /// <summary>The outcome changed the document and its event was recorded.</summary>
    Recorded = 1,

    /// <summary>The document was in that state already (or the outcome is «submitted»): nothing changed.</summary>
    AlreadyRecorded = 2,

    /// <summary>The outcome contradicts the state of the document (a rejection of a delivered one, a delivery of a rejected one): logged and not applied.</summary>
    Ignored = 3,
}

/// <summary>
/// What the SDI said about a document: <see cref="Kind"/> (delivered, rejected; «submitted» is no outcome), for a rejection the code and the text of its notification.
/// </summary>
/// <param name="Kind">One of <see cref="SdiOutcomeKind"/>.</param>
/// <param name="ErrorCode">The code of the notification of rejection.</param>
/// <param name="ErrorDetail">The text of the notification of rejection (kept after <see cref="SdiRejectionReasons.Clean"/>).</param>
/// <param name="OccurredAt">When the state is of (UTC), when the source says it; now otherwise.</param>
public sealed record SdiOutcome(SdiOutcomeKind Kind, string? ErrorCode = null, string? ErrorDetail = null, DateTime? OccurredAt = null)
{
    /// <summary>The outcome of what the provider reports.</summary>
    public static SdiOutcome Of(SdiStatusResult status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new SdiOutcome(status.Status, status.ErrorCode, status.ErrorDetail);
    }
}

/// <summary>The two things an issuer declares of a file it sent by hand.</summary>
public enum SdiDeclarationKind
{
    /// <summary>The file was uploaded to the portal of the Agenzia delle Entrate: the document is <c>manual_declared</c>.</summary>
    Sent = 1,

    /// <summary>The SDI rejected the file the issuer uploaded: the code and the text of its notification.</summary>
    Rejected = 2,
}

/// <summary>The declaration of a file sent through the issuer's own channel.</summary>
/// <param name="Kind">What is declared.</param>
/// <param name="Date">For <see cref="SdiDeclarationKind.Sent"/>: the day of the sending (Europe/Rome), required.</param>
/// <param name="Reference">For <see cref="SdiDeclarationKind.Sent"/>: the reference of the sending (the name of the file, the identifier the portal gave), required.</param>
/// <param name="ErrorCode">For <see cref="SdiDeclarationKind.Rejected"/>: the code of the notification of rejection, required.</param>
/// <param name="ErrorText">For <see cref="SdiDeclarationKind.Rejected"/>: the text of the notification.</param>
public sealed record SdiDeclaration(SdiDeclarationKind Kind, DateOnly? Date = null, string? Reference = null, string? ErrorCode = null, string? ErrorText = null);

/// <summary>A document submitted through the provider and the provider's id of the submission (the system reads of the job and of the webhook).</summary>
public sealed record SdiSubmittedDocument(Guid OrgId, Guid DocumentId, string TransmissionId);

/// <summary>
/// The SDI state of a document as a host reads it (<c>GET api/fiscal/documents/{id}/sdi</c>): the state, the code of what went wrong (the SDI's for a rejected document, the
/// channel's for a document that stays «to send»), when the state is of, and what can be done now.
/// </summary>
/// <param name="DocumentId">The document.</param>
/// <param name="Status">One of <see cref="FiscalDocumentSdiStatuses"/>.</param>
/// <param name="ErrorCode">The code of the rejection, or of the refusal of the channel that left the document «to send».</param>
/// <param name="ErrorDetail">The text kept with it (no personal data).</param>
/// <param name="StatusUpdatedAt">When the state is of (UTC); the declared day for a document declared by hand.</param>
/// <param name="ProviderConfigured">A provider is configured: a submission is attempted (otherwise it answers «not configured»).</param>
/// <param name="CanSubmit">The document is «to send»: it can be submitted, or declared.</param>
/// <param name="CanDeclare">The state accepts a declaration (to send, declared by hand).</param>
/// <param name="CanCorrect">The document was rejected: its correction can be started (E17/F01/US02/T03).</param>
public sealed record FiscalDocumentSdiState(
    Guid DocumentId,
    string Status,
    string? ErrorCode,
    string? ErrorDetail,
    DateTime? StatusUpdatedAt,
    bool ProviderConfigured,
    bool CanSubmit,
    bool CanDeclare,
    bool CanCorrect);

/// <summary>Where an issuer stands with the channel (<see cref="IFiscalDocumentSdiService.IssuerRegistrationsAsync"/>).</summary>
public static class SdiIssuerRegistrationStates
{
    /// <summary>Nothing was submitted for the issuer yet: its VAT number is enabled at the provider at its first submission.</summary>
    public const string Unknown = "unknown";

    /// <summary>The provider accepted a submission of the issuer: its VAT number is enabled.</summary>
    public const string Registered = "registered";

    /// <summary>The provider refused the last submission of the issuer because its VAT number is not enabled yet (<c>sdi_issuer_registration_pending</c>).</summary>
    public const string Pending = "pending";
}

/// <summary>One issuer that sends electronic invoices to the SDI and its state with the channel.</summary>
/// <param name="IssuerProfileId">The issuer profile.</param>
/// <param name="PropertyOwnerId">The owner it is the profile of; <c>null</c> for the profile of the organization.</param>
/// <param name="State">One of <see cref="SdiIssuerRegistrationStates"/>.</param>
public sealed record SdiIssuerRegistration(Guid IssuerProfileId, Guid? PropertyOwnerId, string State);

/// <summary>The stable codes of the errors of the SDI states of the fiscal documents, with the keys of their messages in <c>SharedResources</c>.</summary>
public static class FiscalDocumentSdiErrors
{
    /// <summary>422: the document is not an electronic invoice CasaZen issued (a receipt, a Swiss document, a draft): nothing goes to the SDI.</summary>
    public const string NotElectronic = "sdi_not_electronic";

    /// <summary>409: the document is not «to send» (submitted, declared, delivered or rejected already).</summary>
    public const string NotToSend = "sdi_not_to_send";

    /// <summary>409: the SDI answered already (delivered or rejected): the state does not change any more.</summary>
    public const string StateFinal = "sdi_state_final";

    /// <summary>422: the declaration lacks a field or has an invalid one (the <c>fields</c> are in the answer).</summary>
    public const string DeclarationInvalid = "sdi_declaration_invalid";

    /// <summary>422: the document is not rejected by the SDI: only a rejected one is corrected (a delivered one is credited).</summary>
    public const string NotRejected = "document_not_rejected";

    /// <summary>422: the rejected document cannot be corrected here (an owner invoice, one that has credit notes).</summary>
    public const string NotCorrectable = "document_not_correctable";

    /// <summary>422: the replacement does not replace a rejected document of its issuer, or does not hold its number.</summary>
    public const string ReplacementInvalid = "document_replacement_invalid";

    /// <summary>422: a rejected invoice was never issued, so it is not credited: it is corrected.</summary>
    public const string UseCorrection = "document_rejected_use_correction";

    /// <summary>422: the draft replaces a rejected invoice and holds its number: it is issued (the number goes to it and to nobody else), never discarded, so the numbering has no hole.</summary>
    public const string ReplacementHoldsNumber = "document_replacement_holds_number";

    /// <summary>The key in <c>SharedResources</c> of the message of <paramref name="code"/>.</summary>
    public static string MessageKeyOf(string code) => code switch
    {
        NotElectronic => "SdiNotElectronic",
        NotToSend => "SdiNotToSend",
        StateFinal => "SdiStateFinal",
        DeclarationInvalid => "SdiDeclarationInvalid",
        NotRejected => "FiscalDocumentNotRejected",
        NotCorrectable => "FiscalDocumentNotCorrectable",
        ReplacementInvalid => "FiscalDocumentReplacementInvalid",
        UseCorrection => "FiscalDocumentRejectedUseCorrection",
        ReplacementHoldsNumber => "FiscalDocumentReplacementHoldsNumber",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a code of the SDI states of the documents."),
    };
}

/// <summary>The payload of <c>FiscalDocumentSdiDelivered</c> and <c>FiscalDocumentSdiRejected</c>: ids and codes only.</summary>
public sealed record FiscalDocumentSdiPayload(Guid DocumentId, Guid? BookingId, string Status, string? ErrorCode);

/// <summary>The «To do» of the fiscal documents the SDI states raise (no personal data: a code, the document and its property).</summary>
public static class FiscalDocumentSdiTodos
{
    /// <summary>The SDI rejected an electronic invoice of CasaZen: it is corrected (action <c>CorrectInvoice</c>, entity <c>FiscalDocument</c>).</summary>
    public const string RejectedReason = "sdi_rejected";
}
