using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// How the issued owner invoices are paid (E14/F01/US01/T01, T02): offset in the statement, to pay, or credited (<see cref="OwnerInvoiceSettlements"/>), read now
/// from the owner ledger and the credit notes, as the payment state of a guest document is read from its payments. The one reader of the list, the PDF and the
/// QR-bill.
/// </summary>
internal static class OwnerInvoiceSettlementReader
{
    /// <summary>
    /// The settlement of each issued owner invoice among <paramref name="documents"/> (the other documents are left out): what is left of it after its credit
    /// notes, and the ledger of its owner before its month and at the end of it, its VAT included wherever it was posted.
    /// </summary>
    public static async Task<Dictionary<Guid, OwnerInvoiceSettlement>> ReadAsync(AppDbContext db, IReadOnlyCollection<FiscalDocument> documents, CancellationToken ct)
    {
        var invoices = documents
            .Where(d => d.Purpose == FiscalDocumentPurpose.OwnerInvoice && d.Status == FiscalDocumentStatus.Issued && d.Kind != FiscalDocumentKind.CreditNote
                && d.RecipientOwnerId is not null && d.PeriodYear is not null && d.PeriodMonth is not null)
            .ToList();
        if (invoices.Count == 0)
            return [];

        var ids = invoices.Select(d => d.Id).ToList();
        var orgs = invoices.Select(d => d.OrgId).Distinct().ToList();
        var owners = invoices.Select(d => d.RecipientOwnerId!.Value).Distinct().ToList();

        var credited = await db.FiscalDocuments.AsNoTracking()
            .Where(d => d.CreditedDocumentId != null && ids.Contains(d.CreditedDocumentId.Value) && d.Status == FiscalDocumentStatus.Issued
                && d.Kind == FiscalDocumentKind.CreditNote)
            .GroupBy(d => d.CreditedDocumentId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Sum(d => d.Total) })
            .ToDictionaryAsync(x => x.Id, x => x.Total, ct);

        var months = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => orgs.Contains(e.OrgId) && owners.Contains(e.PropertyOwnerId))
            .GroupBy(e => new { e.OrgId, e.PropertyOwnerId, e.PeriodYear, e.PeriodMonth })
            .Select(g => new { g.Key.OrgId, g.Key.PropertyOwnerId, g.Key.PeriodYear, g.Key.PeriodMonth, Amount = g.Sum(e => e.Amount) })
            .ToListAsync(ct);

        // The VAT of an invoice goes in a later month when its own month of the owner was closed: it is still the invoice's.
        var vat = await db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == OwnerLedgerEntryType.AgencyVat && e.SourceDocumentId != null && ids.Contains(e.SourceDocumentId.Value))
            .Select(e => new { DocumentId = e.SourceDocumentId!.Value, e.PeriodYear, e.PeriodMonth, e.Amount })
            .ToListAsync(ct);

        var result = new Dictionary<Guid, OwnerInvoiceSettlement>();
        foreach (var invoice in invoices)
        {
            var owner = invoice.RecipientOwnerId!.Value;
            var year = invoice.PeriodYear!.Value;
            var month = invoice.PeriodMonth!.Value;
            var ofOwner = months.Where(m => m.OrgId == invoice.OrgId && m.PropertyOwnerId == owner).ToList();
            var opening = ofOwner.Where(m => m.PeriodYear < year || (m.PeriodYear == year && m.PeriodMonth < month)).Sum(m => m.Amount);
            var closing = opening
                + ofOwner.Where(m => m.PeriodYear == year && m.PeriodMonth == month).Sum(m => m.Amount)
                + vat.Where(v => v.DocumentId == invoice.Id && (v.PeriodYear > year || (v.PeriodYear == year && v.PeriodMonth > month))).Sum(v => v.Amount);
            var payable = invoice.Total - credited.GetValueOrDefault(invoice.Id);
            result[invoice.Id] = OwnerInvoiceSettlements.Of(payable, opening, closing);
        }

        return result;
    }
}
