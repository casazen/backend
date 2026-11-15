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
/// </list>
/// </remarks>
public interface IFiscalDocumentCorrectionService
{
    /// <summary>
    /// Starts the correction of the rejected document <paramref name="documentId"/>: the replacement draft (<see cref="FiscalDocument.ReplacesDocumentId"/>), the document replaced
    /// <c>Discarded</c> without its sequence number. 404 <c>fiscal_document_not_found</c>; 422 <c>document_read_only</c> (an imported document), <c>document_owner_invoice</c>,
    /// <c>self_billing_not_correctable</c> (a self-billed invoice, TD17: whatever its state, nothing is made for it, FIX/RES-DOCS), <c>document_not_rejected</c> (any other state of the SDI),
    /// <c>document_not_correctable</c> (it has credit notes).
    /// </summary>
    Task<FiscalDocument> StartCorrectionAsync(Guid documentId, string? userId, CancellationToken ct = default);
}

/// <summary>The payload of <c>FiscalDocumentCorrected</c>: the two documents and their booking, ids only.</summary>
public sealed record FiscalDocumentCorrectedPayload(Guid RejectedDocumentId, Guid ReplacementDocumentId, Guid? BookingId);
