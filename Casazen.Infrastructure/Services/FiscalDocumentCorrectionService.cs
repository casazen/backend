using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFiscalDocumentCorrectionService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One document at a time.</b> The start runs under the transaction-scoped advisory lock <see cref="PostgresAdvisoryLocks.Scope.FiscalDocumentSdi"/> of the rejected document, the lock of its
/// submissions and outcomes, so the SDI answer and a click on «Correggi» never interleave, and two clicks make one draft (the second answers the first).</item>
/// <item><b>The number moves, in the same transaction.</b> The sequence number of the rejected document is cleared in a save of its own, before the replacement holds it: the unique index
/// (<see cref="FiscalDocument.SeriesNumberIndexName"/>) never sees the number twice. A rejected document that never was delivered is the only one this accepts: <c>rejected</c> is reached only
/// by a document that was not delivered (<see cref="FiscalDocumentSdiService"/> never rejects a delivered one), and a document that has an issued credit note is refused; the drafts of
/// credit notes of it are discarded with the correction (they would credit an invoice that never was issued).</item>
/// <item><b>What the replacement is.</b> The same kind, issuer, series, lines, amounts, recipient and period as the rejected document, as stored; once committed it is made again from the booking
/// as it is now, as the refresh of a draft does (a change of the booking while the invoice was rejected, the invoice data the host fixed), and stays the copy when it cannot be made now (the
/// host fixes the data and refreshes it). Its series and number never change. It is a draft: nothing is issued or sent by the correction.</item>
/// <item><b>A self-billed invoice (TD17) is corrected by its own entry point</b> (<see cref="StartSelfBillingCorrectionAsync"/>, FIX/BE-TD17-CORRECTION), which shares everything above with the
/// correction of an invoice to a guest (the lock, the move of the number, the replacement, the event) and differs in what a TD17 differs in: it is read as a TD17 of the organization asked for
/// (another organization's is not found), it is refused with its own code (<see cref="SelfBillingErrors.NotCorrectable"/>), and its replacement is the copy, never made again from a booking (a TD17 has no
/// booking and no guest): the host fixes the issuer profile or the data of the portal, makes the drafts of the month again if the file needs it, and issues it from the self-billed invoices.</item>
/// <item>Logs carry ids: never a value of the document.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentCorrectionService(
    AppDbContext db,
    IDomainEventRecorder events,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentCorrectionService> logger,
    IInternalActivityService? activities = null,
    IFiscalDocumentService? documents = null) : IFiscalDocumentCorrectionService
{
    public async Task<FiscalDocument> StartCorrectionAsync(Guid documentId, string? userId, CancellationToken ct = default)
    {
        await using var transaction = await LockAsync(documentId, ct);

        var document = await db.FiscalDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw NotFound(documentId);
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw new DomainRuleException(FiscalDocumentErrors.ReadOnly, "FiscalDocumentReadOnly");
        if (document.Purpose == FiscalDocumentPurpose.OwnerInvoice)
            throw new DomainRuleException(OwnerInvoiceErrors.UseOwnerInvoices, "FiscalDocumentOwnerInvoice");

        // A self-billed invoice (TD17, E17/F01/US03) is never replaced from here, whatever its state: the replacement is the copy of an invoice to a guest that follows its booking, and a TD17 has
        // no guest and no such flow. Its correction is StartSelfBillingCorrectionAsync, behind its own endpoint and permission. Nothing is made or changed, and the answer says so with a code of its own.
        if (document.Purpose == FiscalDocumentPurpose.SelfBilling)
            throw new DomainRuleException(SelfBillingErrors.NotCorrectable, "SelfBillingNotCorrectable");

        // A second start (a double click, a retry) is the first: the draft the correction already made.
        if (await ReplacementAlreadyMadeAsync(document, ct) is { } existing)
            return existing;

        if (document.Status != FiscalDocumentStatus.Issued || document.SdiStatus != FiscalDocumentSdiStatuses.Rejected)
            throw new DomainRuleException(FiscalDocumentSdiErrors.NotRejected, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotRejected));

        // A document with an issued credit note is not replaced: the credit and the replacement would both bill the same operation (the accountant decides; its «Da fare» says so).
        if (document.SequenceNumber is null
            || string.IsNullOrEmpty(document.Number)
            || await HasIssuedCreditNoteAsync(document.Id, ct))
        {
            throw new DomainRuleException(FiscalDocumentSdiErrors.NotCorrectable, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotCorrectable));
        }

        var (replacement, discardedCredits) = await ReplaceAsync(document, userId, transaction, ct);

        // The replacement follows the booking as it is now, as the refresh of a draft does: a change of the booking while the invoice was rejected (its «To do» gave way to the one of the
        // correction) and the invoice data the host fixed are in it. When it cannot be made now (the data the SDI refused is still missing, the booking is no longer active), it stays the copy
        // of the rejected invoice, for the host to fix and refresh. Its series and number never change.
        if (documents is not null)
        {
            try
            {
                replacement = await documents.RefreshDraftAsync(replacement.Id, ct);
            }
            catch (Exception ex) when (ex is DomainException or NotFoundException)
            {
                logger.LogInformation(
                    "Replacement draft {ReplacementId} not made again from its booking now ({Code}): it keeps the lines of the rejected invoice",
                    replacement.Id,
                    ex is DomainException refusal ? refusal.Code : "booking_not_found");
            }
        }

        logger.LogInformation(
            "Fiscal document {DocumentId} of org {OrgId} rejected by the SDI: correction started, replacement draft {ReplacementId}, {Discarded} credit note drafts discarded",
            document.Id,
            document.OrgId,
            replacement.Id,
            discardedCredits);
        return replacement;
    }

    public async Task<FiscalDocument> StartSelfBillingCorrectionAsync(Guid orgId, Guid documentId, string? userId, CancellationToken ct = default)
    {
        await using var transaction = await LockAsync(documentId, ct);

        // The TD17 of the organization asked for: a document of another organization, a credit note, a document that is not a TD17 and an imported one are not found (404), never refused.
        var document = await db.FiscalDocuments
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(
                d => d.Id == documentId && d.OrgId == orgId && d.Purpose == FiscalDocumentPurpose.SelfBilling && d.Kind != FiscalDocumentKind.CreditNote && d.Source == FiscalDocumentSource.CasaZen,
                ct)
            ?? throw NotFound(documentId);

        // A second start (a double click, a retry) is the first: the draft the correction already made, while it is a draft.
        if (await ReplacementAlreadyMadeAsync(document, ct) is { } existing)
            return existing;

        // Only a TD17 the SDI rejected and that nobody credited is replaced: whatever else it is (a draft, to send, submitted, delivered, replaced and issued already), nothing is made or changed.
        if (!SelfBillingRules.IsCorrectable(document, await HasIssuedCreditNoteAsync(document.Id, ct)))
            throw new DomainRuleException(SelfBillingErrors.NotCorrectable, "SelfBillingCorrectionNotApplicable");

        var (replacement, _) = await ReplaceAsync(document, userId, transaction, ct);
        logger.LogInformation(
            "Self-billed invoice {DocumentId} of org {OrgId} rejected by the SDI: correction started, replacement draft {ReplacementId}",
            document.Id,
            document.OrgId,
            replacement.Id);
        return replacement;
    }

    /// <summary>The transaction of the correction, under the lock of the SDI states of the document (the one a submission and an outcome take): <c>null</c> where the provider has no such lock.</summary>
    private Task<IDbContextTransaction?> LockAsync(Guid documentId, CancellationToken ct) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.FiscalDocumentSdi, documentId.ToString("N")));

    /// <summary>
    /// The draft a correction of <paramref name="document"/> already made: the document is the rejected original the correction discarded and a draft replaces it. <c>null</c> when it
    /// is not (the first start, or the replacement was issued since).
    /// </summary>
    private async Task<FiscalDocument?> ReplacementAlreadyMadeAsync(FiscalDocument document, CancellationToken ct)
    {
        if (document.Status != FiscalDocumentStatus.Discarded || document.SdiStatus != FiscalDocumentSdiStatuses.Rejected)
            return null;

        return await db.FiscalDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReplacesDocumentId == document.Id && d.Status == FiscalDocumentStatus.Draft, ct);
    }

    /// <summary>The document has a credit note issued: a rejected one is then not corrected (the accountant decides). The draft of a credit note does not count: the correction discards it.</summary>
    private Task<bool> HasIssuedCreditNoteAsync(Guid documentId, CancellationToken ct) =>
        db.FiscalDocuments.AnyAsync(d => d.CreditedDocumentId == documentId && d.Status == FiscalDocumentStatus.Issued, ct);

    /// <summary>
    /// The correction itself, for a document the caller checked (rejected, with its number, no credit note issued) and holds the lock of. The rejected document counts as not issued: it keeps its
    /// display number for the history, gives up its sequence number and is discarded, and the replacement draft holds that series and number from the start. The drafts of credit notes of the
    /// rejected document (prepared by a change of its booking while it was with the SDI) would credit an invoice that never was issued, and could not be issued anyway: they are discarded with it,
    /// and their «Da fare» closed. The event <c>FiscalDocumentCorrected</c> is recorded in the same unit of work, and the transaction (<c>null</c> where the provider has none) is committed.
    /// </summary>
    private async Task<(FiscalDocument Replacement, int DiscardedCredits)> ReplaceAsync(FiscalDocument document, string? userId, IDbContextTransaction? transaction, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sequenceNumber = document.SequenceNumber!.Value;

        document.SequenceNumber = null;
        document.Status = FiscalDocumentStatus.Discarded;
        document.UpdatedAt = now;
        var plannedCredits = await db.FiscalDocuments
            .Where(d => d.CreditedDocumentId == document.Id && d.Status == FiscalDocumentStatus.Draft)
            .ToListAsync(ct);
        foreach (var note in plannedCredits)
        {
            note.Status = FiscalDocumentStatus.Discarded;
            note.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        await CloseTodosOfAsync(document.OrgId, plannedCredits, ct);

        var replacement = ReplacementOf(document, sequenceNumber, userId, now);
        db.FiscalDocuments.Add(replacement);
        events.Record(
            DomainEventType.FiscalDocumentCorrected,
            document.Id,
            new FiscalDocumentCorrectedPayload(document.Id, replacement.Id, document.BookingId),
            dedupKey: $"fiscal-document-corrected:{document.Id:N}",
            orgId: document.OrgId);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return (replacement, plannedCredits.Count);
    }

    /// <summary>The open «Da fare» of the credit note drafts the correction discarded («check and issue the credit note»): nothing is left to issue.</summary>
    private async Task CloseTodosOfAsync(Guid orgId, IReadOnlyList<FiscalDocument> discarded, CancellationToken ct)
    {
        if (activities is null)
            return;

        foreach (var note in discarded)
        {
            if (await activities.FindOpenAsync(orgId, nameof(ActivityAction.ReviewFiscalDocument), FiscalDocumentTodos.EntityType, note.Id, ct) is { } open)
                await activities.CloseAsync(open.Id, InternalActivity.ClosedBySystem, ct);
        }
    }

    /// <summary>
    /// The draft that replaces <paramref name="rejected"/>: its lines, amounts and recipient as stored, its issuer, its part of the booking (or, for a self-billed invoice, its month: the draft is
    /// made again and issued by it) and the series and sequence number it gave up. The lines are added to the new document before it is added to the context, so they are inserted with it.
    /// </summary>
    private static FiscalDocument ReplacementOf(FiscalDocument rejected, int sequenceNumber, string? userId, DateTime now)
    {
        var replacement = new FiscalDocument
        {
            OrgId = rejected.OrgId,
            BookingId = rejected.BookingId,
            PropertyId = rejected.PropertyId,
            Purpose = rejected.Purpose,
            IssuerProfileId = rejected.IssuerProfileId,
            ForAgencyServices = rejected.ForAgencyServices,
            PeriodYear = rejected.PeriodYear,
            PeriodMonth = rejected.PeriodMonth,
            ServicePeriodFrom = rejected.ServicePeriodFrom,
            ServicePeriodTo = rejected.ServicePeriodTo,
            RuleVersion = rejected.RuleVersion,
            PaymentChannel = rejected.PaymentChannel,
            PaymentState = rejected.PaymentState,
            Source = FiscalDocumentSource.CasaZen,
            Kind = rejected.Kind,
            Status = FiscalDocumentStatus.Draft,
            Currency = rejected.Currency,
            TaxableBase = rejected.TaxableBase,
            VatAmount = rejected.VatAmount,
            StampDuty = rejected.StampDuty,
            Total = rejected.Total,
            VatRatePercent = rejected.VatRatePercent,
            VatNatureCode = rejected.VatNatureCode,
            CreditedDocumentId = rejected.CreditedDocumentId,
            ReplacesDocumentId = rejected.Id,

            // The number the correction took from the rejected document, held by the draft from the start: the issue uses it and reserves no other.
            Series = rejected.Series,
            SequenceNumber = sequenceNumber,
            Number = rejected.Number,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
        };
        FiscalDocumentService.CopyRecipient(rejected, replacement);
        foreach (var line in rejected.Lines.OrderBy(l => l.LineNumber))
            replacement.Lines.Add(FiscalDocumentService.CopyLine(replacement, line));

        return replacement;
    }

    private static NotFoundException NotFound(Guid documentId) =>
        new($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };
}
