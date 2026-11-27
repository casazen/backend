using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The correction of an electronic invoice the SDI rejected (E17/F01/US02/T03, NF-86, ADR-026, ADR-022): it counts as not issued (nothing reached its recipient), so it is
/// replaced by a new draft with the same lines and recipient, that keeps its number, instead of being edited or credited. A document that was delivered is corrected with a
/// credit note, never edited.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The number.</b> The rejected document keeps its display number for the history, loses its sequence number and becomes <c>Discarded</c>; the replacement draft holds
/// that series and sequence number from the start, and the issue (<see cref="IFiscalDocumentService.IssueAsync"/>) uses them instead of reserving a new one. The number goes to the replacement
/// of that document and to nobody else, and only because the document never was delivered (a delivered document is never rejected afterwards, so a rejected one never was).
/// ⚖ The reuse of the number, and the date it keeps, are to be confirmed by the accountant. A replacement that is discarded leaves its number unused (the history says why).</item>
/// <item><b>Idempotent.</b> A second start on the same rejected document answers the replacement draft it already made.</item>
/// <item><b>Events.</b> <c>FiscalDocumentCorrected</c> is recorded in the unit of work of the correction; its consumer closes the «To do» of the rejection.</item>
/// <item><b>Two entry points, one correction.</b> An invoice to a guest is corrected by <see cref="StartCorrectionAsync"/> (the replacement follows its booking as it is now); a self-billed invoice
/// (TD17, E17/F01/US03) by <see cref="StartSelfBillingCorrectionAsync"/> (no booking, no guest: a plain draft the host fixes and issues from the self-billed invoices). Both take the number from the
/// rejected document and give it to its replacement in the same way, in the same transaction, under the same lock; neither one touches the documents of the other.</item>
/// </list>
/// </remarks>
public interface IFiscalDocumentCorrectionService
{
    /// <summary>
    /// Starts the correction of the rejected document <paramref name="documentId"/>: the replacement draft (<see cref="FiscalDocument.ReplacesDocumentId"/>), the document replaced
    /// <c>Discarded</c> without its sequence number. 404 <c>fiscal_document_not_found</c>; 422 <c>document_read_only</c> (an imported document), <c>document_owner_invoice</c>,
    /// <c>self_billing_not_correctable</c> (a self-billed invoice, TD17: whatever its state, nothing is made for it here: <see cref="StartSelfBillingCorrectionAsync"/> is its correction, FIX/RES-DOCS),
    /// <c>document_not_rejected</c> (any other state of the SDI), <c>document_not_correctable</c> (it has credit notes).
    /// </summary>
    Task<FiscalDocument> StartCorrectionAsync(Guid documentId, string? userId, CancellationToken ct = default);

    /// <summary>
    /// Starts the correction of the self-billed invoice (TD17) <paramref name="documentId"/> of the organization <paramref name="orgId"/> that the SDI rejected (FIX/BE-TD17-CORRECTION): for the tax
    /// office it was never issued, so it is replaced by a draft with its series and its number (<see cref="FiscalDocument.ReplacesDocumentId"/>), and the rejected one keeps its display number for
    /// the history, gives up its sequence number and becomes <c>Discarded</c>. The replacement copies the lines, the amounts, the supplier (the foreign portal) and the issuer as stored and is
    /// <b>nothing more than a draft</b>: it is not made again from a booking (a TD17 has none), nothing is issued and nothing goes to the SDI. The host fixes what the SDI refused (the issuer
    /// profile, the data of the portal), makes the drafts of the month again if the file needs it, and issues it from the self-billed invoices, which uses the held number and sends it as any other.
    /// A second start answers the replacement the first one made, while it is a draft. 404 <c>fiscal_document_not_found</c> (another organization, not a TD17, not found); 422
    /// <c>self_billing_not_correctable</c> (it is not issued and rejected by the SDI, or it has an issued credit note).
    /// </summary>
    Task<FiscalDocument> StartSelfBillingCorrectionAsync(Guid orgId, Guid documentId, string? userId, CancellationToken ct = default);
}

/// <summary>The payload of <c>FiscalDocumentCorrected</c>: the two documents and their booking, ids only.</summary>
public sealed record FiscalDocumentCorrectedPayload(Guid RejectedDocumentId, Guid ReplacementDocumentId, Guid? BookingId);
