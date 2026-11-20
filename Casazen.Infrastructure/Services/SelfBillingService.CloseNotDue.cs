using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The exit of a replacement draft of a rejected self-billed invoice (TD17) that can no longer be sustained: «Chiusa, non più dovuta» (FIX/BE-TD17-JOB-EXIT, decision 3 of the fiscal decisions of
/// 2026-11-20, research R3 of the same day).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>An internal outcome, never «scarto confermato».</b> The rejection is an external fact of the SDI and stays on the rejected original, as history. Closing is a decision of the host about the replacement
/// draft the correction made (<see cref="FiscalDocument.ReplacesDocumentId"/>): the commissions were reversed, the holder is no longer subject, the portal entry was removed (the drafts the system reports
/// with <see cref="SelfBillingRow.KeptReason"/>). The draft becomes <see cref="FiscalDocumentStatus.Discarded"/> with the reason, the user and the day.</item>
/// <item><b>The number is never freed.</b> The draft keeps its series, its sequence number and its display number: the unique index of the series (<see cref="FiscalDocument.SeriesNumberIndexName"/>)
/// never lets the number be taken again, the counter of the series is already past it, and a check of the table (<c>CK_FiscalDocuments_ClosedNotDue</c>) refuses a closed draft without its number. It is a
/// documented gap: the register of the unused numbers of the accountant package lists it. The reuse of the number (only when it is the last of the series and no later number was issued) stays off and is not
/// built: no official source says whether the number of an abandoned rejected invoice may be reassigned, so the prudent reading is a documented gap (art. 21 DPR 633/72, Ris. 1/E 2013).</item>
/// <item><b>Only what the system reports as kept.</b> The draft must replace a rejected TD17, hold its number and be one the drafts of its month would keep (the same read as the list and the detail: a draft the
/// month still gives, or would adopt, is issued, not closed). Anything else (an issued TD17, an ordinary draft, a discarded one) is 422 <c>self_billing_not_closable</c> and nothing is changed.</item>
/// <item><b>One decision at a time.</b> It is taken under the lock of the generation of the organization (<see cref="PostgresAdvisoryLocks.Scope.SelfBilling"/>): the month cannot adopt or make again the draft
/// meanwhile, and the same call twice (a double click, a retry) answers the closed draft once the first is done (the same reason; another reason is refused, so who closed it and why is never rewritten).</item>
/// <item><b>Tenant.</b> The document is read by the organization of the caller (another organization's is 404). The note is free text: it is stored encrypted and never logged; the log carries ids and the code of the reason.</item>
/// </list>
/// </remarks>
public sealed partial class SelfBillingService
{
    public async Task<SelfBillingDetail> CloseNotDueAsync(Guid orgId, Guid documentId, string? reason, string? note, string? userId, CancellationToken ct = default)
    {
        // The reason is mandatory and is one of the three the screen offers; a note, when there is one, is short. Nothing is read before this: a refusal changes nothing.
        var closeReason = SelfBillingCloseReasons.Normalize(reason)
            ?? throw new DomainRuleException(SelfBillingErrors.CloseReasonRequired, "SelfBillingCloseReasonRequired");
        var closeNote = Clean(note);
        if (closeNote is { Length: > FiscalDocument.ClosedNotDueNoteMaxLength })
            throw new DomainRuleException(SelfBillingErrors.CloseNoteTooLong, "SelfBillingCloseNoteTooLong", FiscalDocument.ClosedNotDueNoteMaxLength);

        // The decision and what it reads (the month the draft would be kept in) are taken under the lock of the generation of the organization.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.SelfBilling, orgId.ToString("N")));

        // The TD17 of the organization asked for: a document of another organization, a credit note, a document that is not a TD17 and an imported one are not found (404), never refused.
        var document = await db.FiscalDocuments
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.Kind != DocumentKind.CreditNote && d.Source == FiscalDocumentSource.CasaZen,
                ct)
            ?? throw DocumentNotFound(documentId);

        // Closed already: the same reason answers the same closed draft (a double click, a retry); another reason is refused, the record of who closed it and why is never rewritten.
        if (document.Status == FiscalDocumentStatus.Discarded && document.ClosedNotDueAt is not null)
        {
            if (!string.Equals(document.ClosedNotDueReason, closeReason, StringComparison.Ordinal))
                throw NotClosable();

            return await DetailOfAsync(orgId, document, 0m, ct);
        }

        // Only the replacement of a rejected TD17, still a draft and still holding its number, whose month no longer gives it a TD17 (the reason the list and the detail show as keptReason).
        if (document.Status != FiscalDocumentStatus.Draft
            || !FiscalDocumentService.HoldsAReplacedNumber(document)
            || document.PeriodYear is not { } year
            || document.PeriodMonth is not { } month
            || !(await KeptReasonsAsync(orgId, [document], ct)).ContainsKey(document.Id))
        {
            throw NotClosable();
        }

        // The draft leaves, and its number stays where it is: series, sequence number and display number are not touched (a closed draft without its number would be refused by the table).
        var now = UtcNow;
        document.Status = FiscalDocumentStatus.Discarded;
        document.ClosedNotDueReason = closeReason;
        document.ClosedNotDueNote = closeNote;
        document.ClosedNotDueAt = now;
        document.ClosedNotDueByUserId = Clean(userId) is { } closer ? Truncate(closer, FiscalDocument.CreatedByUserIdMaxLength) : null;
        document.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Self-billed invoice draft {DocumentId} of org {OrgId} closed as no longer due ({Reason}) by {UserId}: its number stays reserved",
            document.Id,
            orgId,
            closeReason,
            document.ClosedNotDueByUserId);

        // The last open draft of the month closed: its «Da fare» to check and issue is done (best effort after the change is saved, as for a discard).
        await TrySyncDraftsTodoAsync(orgId, year, month, null, ct);
        return await DetailOfAsync(orgId, document, 0m, ct);
    }

    private static DomainRuleException NotClosable() => new(SelfBillingErrors.NotClosable, "SelfBillingNotClosable");
}
