using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IFiscalDocumentCorrectionService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One document at a time.</b> The start runs under the transaction-scoped advisory lock <see cref="PostgresAdvisoryLocks.Scope.FiscalDocumentSdi"/> of the rejected document, the lock of its
/// submissions and outcomes, so the SDI answer and a click on «Correggi» never interleave, and two clicks make one draft (the second answers the first).</item>
/// <item><b>The number moves, in the same transaction.</b> The sequence number of the rejected document is cleared in a save of its own, before the replacement holds it: the unique index
/// (<see cref="FiscalDocument.SeriesNumberIndexName"/>) never sees the number twice. A rejected document that never was delivered is the only one this accepts: <c>rejected</c> is reached only
/// by a document that was not delivered (<see cref="FiscalDocumentSdiService"/> never rejects a delivered one), and a document that has credit notes is refused.</item>
/// <item><b>What the replacement is.</b> The same kind, issuer, series, lines, amounts, recipient and period as the rejected document, as stored (never made again from the booking here: the host
/// fixes the data, and the refresh of the draft or the daily run makes it from the booking again). It is a draft: nothing is issued or sent by the correction.</item>
/// <item>Logs carry ids: never a value of the document.</item>
/// </list>
/// </remarks>
public sealed class FiscalDocumentCorrectionService(
    AppDbContext db,
    IDomainEventRecorder events,
    TimeProvider timeProvider,
    ILogger<FiscalDocumentCorrectionService> logger) : IFiscalDocumentCorrectionService
{
    public async Task<FiscalDocument> StartCorrectionAsync(Guid documentId, string? userId, CancellationToken ct = default)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, ct, (PostgresAdvisoryLocks.Scope.FiscalDocumentSdi, documentId.ToString("N")));

        var document = await db.FiscalDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.Id == documentId, ct)
            ?? throw new NotFoundException($"Fiscal document {documentId} was not found") { Code = FiscalDocumentErrors.NotFound, MessageKey = "FiscalDocumentNotFound" };
        if (document.Source != FiscalDocumentSource.CasaZen)
            throw new DomainRuleException(FiscalDocumentErrors.ReadOnly, "FiscalDocumentReadOnly");
        if (document.Purpose == FiscalDocumentPurpose.OwnerInvoice)
            throw new DomainRuleException(OwnerInvoiceErrors.UseOwnerInvoices, "FiscalDocumentOwnerInvoice");

        // A second start (a double click, a retry) is the first: the draft the correction already made.
        if (document.Status == FiscalDocumentStatus.Discarded && document.SdiStatus == FiscalDocumentSdiStatuses.Rejected
            && await db.FiscalDocuments.Include(d => d.Lines).FirstOrDefaultAsync(d => d.ReplacesDocumentId == document.Id && d.Status == FiscalDocumentStatus.Draft, ct) is { } existing)
        {
            return existing;
        }

        if (document.Status != FiscalDocumentStatus.Issued || document.SdiStatus != FiscalDocumentSdiStatuses.Rejected)
            throw new DomainRuleException(FiscalDocumentSdiErrors.NotRejected, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotRejected));

        // A document with credit notes (issued or still drafts) is not replaced: the credit and the replacement would both bill the same operation.
        if (document.SequenceNumber is null
            || string.IsNullOrEmpty(document.Number)
            || await db.FiscalDocuments.AnyAsync(d => d.CreditedDocumentId == document.Id && d.Status != FiscalDocumentStatus.Discarded, ct))
        {
            throw new DomainRuleException(FiscalDocumentSdiErrors.NotCorrectable, FiscalDocumentSdiErrors.MessageKeyOf(FiscalDocumentSdiErrors.NotCorrectable));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sequenceNumber = document.SequenceNumber.Value;

        // The rejected document counts as not issued: it keeps its display number for the history, gives up its sequence number and is discarded.
        document.SequenceNumber = null;
        document.Status = FiscalDocumentStatus.Discarded;
        document.UpdatedAt = now;
        await db.SaveChangesAsync(ct);

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

        logger.LogInformation("Fiscal document {DocumentId} of org {OrgId} rejected by the SDI: correction started, replacement draft {ReplacementId}", document.Id, document.OrgId, replacement.Id);
        return replacement;
    }

    /// <summary>
    /// The draft that replaces <paramref name="rejected"/>: its lines, amounts and recipient as stored, its issuer, its part of the booking and the series and sequence number it gave up. The
    /// lines are added to the new document before it is added to the context, so they are inserted with it.
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
}
