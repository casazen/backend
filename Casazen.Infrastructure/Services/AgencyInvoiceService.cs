using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The agency-to-owner invoices of the retired internal generator (ADR-019 amended, E06/F01/US02/T05, AC-NF110-6), read-only. Nothing writes this table any
/// more: the invoice of the property manager to an owner is a fiscal document made from the statement version since E14/F01/US01/T01
/// (<see cref="IOwnerInvoiceService"/>; the old generate endpoint makes those drafts instead of answering 410). The historical rows stay readable as they were
/// stored, marked <c>legacy_unverified</c>, never relabelled as compliant.
/// </summary>
public sealed class AgencyInvoiceService(AppDbContext db) : IAgencyInvoiceService
{
    /// <inheritdoc />
    public async Task<string?> GetInvoiceXmlAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Set<AgencyInvoice>()
            .Where(i => i.Id == invoiceId)
            .FirstOrDefaultAsync(cancellationToken);

        if (invoice is null)
        {
            throw new DomainRuleException("invoice_not_found", "InvoiceNotFound");
        }

        if (invoice.Status == InvoiceStatus.Draft)
        {
            throw new DomainRuleException("invoice_not_finalized", "InvoiceNotFinalized");
        }

        return invoice.FatturapaXml;
    }

    /// <inheritdoc />
    public async Task<byte[]> GetInvoicePdfAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var invoice = await db.Set<AgencyInvoice>()
            .Include(i => i.PropertyOwner)
            .Include(i => i.Org)
            .Where(i => i.Id == invoiceId)
            .FirstOrDefaultAsync(cancellationToken);

        if (invoice is null)
        {
            throw new DomainRuleException("invoice_not_found", "InvoiceNotFound");
        }

        if (invoice.Status == InvoiceStatus.Draft)
        {
            throw new DomainRuleException("invoice_not_finalized", "InvoiceNotFinalized");
        }

        // Generate PDF (simplified implementation - full PDF generation would use a library like QuestPDF)
        return GenerateInvoicePdf(invoice);
    }


    /// <summary>
    /// The courtesy copy of a historical invoice as it was stored (a text rendering): the date is the one it was made on, never the day it is downloaded (a
    /// historical fiscal document is not dated again), with its compliance code (<c>legacy_unverified</c>).
    /// </summary>
    private static byte[] GenerateInvoicePdf(AgencyInvoice invoice)
    {
        // Simplified PDF generation - return a simple text representation as bytes
        // In production, this would use QuestPDF or another PDF library
        var pdfContent = $@"INVOICE
================

Invoice Number: {invoice.Series}{invoice.InvoiceNumber}
Date: {invoice.CreatedAt:yyyy-MM-dd}
Period: {invoice.Month:D2}/{invoice.Year}

From: {invoice.Org.Name}
To: {invoice.PropertyOwner.FirstName} {invoice.PropertyOwner.LastName}

Taxable Base: €{invoice.TaxableBase:F2}
VAT ({invoice.VatRate:F2}%): €{invoice.VatAmount:F2}
Stamp Duty: €{invoice.StampDuty:F2}

TOTAL: €{invoice.Total:F2}

Status: {invoice.Status}
Compliance: {invoice.ComplianceStatus}
";

        return Encoding.UTF8.GetBytes(pdfContent);
    }
}
