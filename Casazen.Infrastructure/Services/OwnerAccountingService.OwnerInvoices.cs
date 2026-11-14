using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The lines an owner invoice posts in the owner ledger (E14/F01/US01/T01, ⚖ R4): the VAT it added on top, at its issue, and its reversal by a credit note;
/// and the new version of the month they leave behind. They run in the transaction of the issue (the lock of the owner accounting joins it), so the VAT is
/// posted with the number of the invoice or not at all.
/// </summary>
public sealed partial class OwnerAccountingService
{
    /// <summary>The prefix of the key of a line of an owner invoice: <c>invoicevat:{documentId:N}</c>, once per document.</summary>
    internal const string OwnerInvoiceVatKeyPrefix = "invoicevat:";

    public async Task<OwnerLedgerPosting?> PostOwnerInvoiceVatAsync(FiscalDocument invoice, decimal vat, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (vat <= 0m)
            return null;
        var (ownerId, period) = OwnerAndPeriodOf(invoice);

        await using var tx = await LockAsync(invoice.OrgId, ct);
        var key = OwnerInvoiceVatKeyPrefix + invoice.Id.ToString("N");
        if (await db.OwnerLedgerEntries.AsNoTracking().AnyAsync(e => e.OrgId == invoice.OrgId && e.IdempotencyKey == key, ct))
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        var into = await OpenPeriodForAsync(invoice.OrgId, ownerId, period, ct);
        db.OwnerLedgerEntries.Add(new OwnerLedgerEntry
        {
            OrgId = invoice.OrgId,
            PropertyOwnerId = ownerId,
            EntryType = OwnerLedgerEntryType.AgencyVat,
            SourceType = OwnerLedgerSourceType.OwnerInvoice,
            SourceId = invoice.Id,
            SourceReference = Truncate(invoice.Number, OwnerLedgerEntry.ReferenceMaxLength),
            SourceDocumentId = invoice.Id,
            IdempotencyKey = key,
            ServiceDate = period.Last,
            RecognitionDate = into == period ? period.Last : into.First,
            PeriodYear = into.Year,
            PeriodMonth = into.Month,
            SharePercent = 100m,
            OriginalCurrency = invoice.Currency,
            SourceAmount = -vat,
            OriginalAmount = -vat,
            StatementCurrency = invoice.Currency,
            FxRate = 1m,
            RuleVersion = Truncate(invoice.RuleVersion, OwnerLedgerEntry.RuleVersionMaxLength),
            Amount = -vat,
            PostedAt = now,
            PostedByUserId = userId,
        });
        await MarkDirtyAsync(invoice.OrgId, into, [ownerId], now, ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "VAT of owner invoice {DocumentId} posted to the ledger of owner {OwnerId} in {Year}-{Month}", invoice.Id, ownerId, into.Year, into.Month);
        return new OwnerLedgerPosting(ownerId, into.Year, into.Month);
    }

    public async Task<OwnerLedgerPosting?> ReverseOwnerInvoiceVatAsync(FiscalDocument creditNote, FiscalDocument invoice, string? userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(creditNote);
        ArgumentNullException.ThrowIfNull(invoice);
        var (ownerId, period) = OwnerAndPeriodOf(invoice);

        await using var tx = await LockAsync(invoice.OrgId, ct);
        var lines = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.OrgId == invoice.OrgId && e.EntryType == OwnerLedgerEntryType.AgencyVat && e.SourceDocumentId == invoice.Id)
            .ToListAsync(ct);
        var posted = lines.FirstOrDefault(e => e.SourceId == invoice.Id && e.ReversesEntryId is null);
        if (posted is null || lines.Any(e => e.ReversesEntryId == posted.Id))
            return null;

        // What the earlier credit notes of the invoice gave back already, and what is left of the VAT the invoice posted.
        var given = lines.Where(e => e.Id != posted.Id && e.ReversesEntryId is null).Sum(e => e.Amount);
        var left = -posted.Amount - given;
        if (left <= 0m)
            return null;

        // The whole VAT left with the note that completes the credit (the cents of the partial notes included), else the VAT of the note.
        var amount = invoice.CreditedInFull ? left : Math.Min(creditNote.VatAmount, left);
        if (amount <= 0m)
            return null;

        var now = time.GetUtcNow().UtcDateTime;
        var into = await OpenPeriodForAsync(invoice.OrgId, ownerId, new Period(posted.PeriodYear, posted.PeriodMonth), ct);
        if (given == 0m && amount == -posted.Amount)
        {
            db.OwnerLedgerEntries.Add(Reversal(posted, into, now, userId));
        }
        else
        {
            db.OwnerLedgerEntries.Add(new OwnerLedgerEntry
            {
                OrgId = invoice.OrgId,
                PropertyOwnerId = ownerId,
                EntryType = OwnerLedgerEntryType.AgencyVat,
                SourceType = OwnerLedgerSourceType.OwnerInvoice,
                SourceId = creditNote.Id,
                SourceReference = Truncate(creditNote.Number, OwnerLedgerEntry.ReferenceMaxLength),
                SourceDocumentId = invoice.Id,
                IdempotencyKey = OwnerInvoiceVatKeyPrefix + creditNote.Id.ToString("N"),
                ServiceDate = period.Last,
                RecognitionDate = into.First,
                PeriodYear = into.Year,
                PeriodMonth = into.Month,
                SharePercent = 100m,
                OriginalCurrency = invoice.Currency,
                SourceAmount = amount,
                OriginalAmount = amount,
                StatementCurrency = invoice.Currency,
                FxRate = 1m,
                RuleVersion = Truncate(invoice.RuleVersion, OwnerLedgerEntry.RuleVersionMaxLength),
                Amount = amount,
                PostedAt = now,
                PostedByUserId = userId,
            });
        }

        await MarkDirtyAsync(invoice.OrgId, into, [ownerId], now, ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
            await tx.CommitAsync(ct);

        logger.LogInformation(
            "VAT of owner invoice {DocumentId} given back by credit note {CreditNoteId} to owner {OwnerId} in {Year}-{Month}",
            invoice.Id, creditNote.Id, ownerId, into.Year, into.Month);
        return new OwnerLedgerPosting(ownerId, into.Year, into.Month);
    }

    public async Task<OwnerStatementVersion?> RegenerateOpenVersionAsync(Guid orgId, Guid ownerId, int year, int month, string? userId, CancellationToken ct = default)
    {
        Period.Of(year, month);
        var current = await db.OwnerStatementVersions.AsNoTracking()
            .Where(v => v.OrgId == orgId && v.PropertyOwnerId == ownerId && v.PeriodYear == year && v.PeriodMonth == month
                && v.Status != OwnerStatementVersionStatus.Superseded)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync(ct);
        if (current is not { Status: OwnerStatementVersionStatus.Draft or OwnerStatementVersionStatus.Flagged })
            return null;

        return await GenerateVersionAsync(orgId, ownerId, year, month, userId, ct);
    }

    /// <summary>The month a line of an owner invoice goes in: <paramref name="period"/> while it is open for the owner, else the month of today (Rome).</summary>
    private async Task<Period> OpenPeriodForAsync(Guid orgId, Guid ownerId, Period period, CancellationToken ct)
    {
        var closed = await ClosedMonthsAsync(orgId, [ownerId], ct);
        return closed.Contains((ownerId, period.Year, period.Month)) ? Period.Of(time.TodayInRomeAsDateOnly()) : period;
    }

    private static (Guid OwnerId, Period Period) OwnerAndPeriodOf(FiscalDocument invoice)
    {
        if (invoice.Purpose != FiscalDocumentPurpose.OwnerInvoice || invoice.RecipientOwnerId is not { } ownerId
            || invoice.PeriodYear is not { } year || invoice.PeriodMonth is not { } month)
        {
            throw new ArgumentException("The document is not an owner invoice with its owner and period.", nameof(invoice));
        }

        return (ownerId, Period.Of(year, month));
    }
}
