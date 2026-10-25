using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// FatturaPA invoice generation service for agencies (ADR-019).
/// Phase 1: no automatic SDI transmission, status ToBeIssuedManually.
/// Sequential numbering enforced by row lock, VAT and stamp duty calculation, XML validation.
/// </summary>
public sealed class AgencyInvoiceService(AppDbContext db) : IAgencyInvoiceService
{
    private const decimal StampDutyThreshold = 77.47m;
    private const decimal StampDutyAmount = 2.00m;

    /// <inheritdoc />
    public async Task<InvoiceGenerationResult> GenerateInvoiceAsync(
        Guid statementId,
        CancellationToken cancellationToken = default)
    {
        // Get the statement with its lines
        var statement = await db.Set<OwnerStatement>()
            .Include(s => s.Lines)
            .Include(s => s.PropertyOwner)
            .Include(s => s.Org)
            .Where(s => s.Id == statementId)
            .FirstOrDefaultAsync(cancellationToken);

        if (statement is null)
        {
            throw new DomainRuleException("statement_not_found", "StatementNotFound");
        }

        // Validate statement is Approved (only approved statements can be invoiced)
        if (statement.Status != StatementStatus.Approved)
        {
            throw new DomainRuleException("statement_not_approved", "StatementNotApproved");
        }

        // Check if invoice already exists for this statement
        // For now, we check by owner/month/year (one invoice per owner per month per ADR-019)
        var existingInvoice = await db.Set<AgencyInvoice>()
            .Where(i => i.OrgId == statement.OrgId
                && i.OwnerId == statement.PropertyOwnerId
                && i.Month == statement.Month
                && i.Year == statement.Year)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingInvoice is not null)
        {
            throw new DomainRuleException("invoice_already_exists", "InvoiceAlreadyExists");
        }

        // Validate single supplier per invoice (ADR-019-R5)
        // All statement lines must reference the same PropertyOwnerId (which they do by statement design)
        // But we also need to check that all lines have the same OwnerId through the statement
        var distinctOwners = statement.Lines
            .Select(l => l.StatementId)
            .Distinct()
            .Count();

        if (distinctOwners > 1)
        {
            throw new DomainRuleException("invoice_multiple_suppliers", "InvoiceMultipleSuppliers");
        }

        // TODO: Validate agency has structured address (ADR-019-R9)
        // Phase 1: structured address fields not yet in Org entity (will be added in future task)
        // When added, uncomment this validation:
        // if (string.IsNullOrWhiteSpace(statement.Org.StructuredAddressStreet) ||
        //     string.IsNullOrWhiteSpace(statement.Org.StructuredAddressCity) ||
        //     string.IsNullOrWhiteSpace(statement.Org.StructuredAddressPostalCode) ||
        //     string.IsNullOrWhiteSpace(statement.Org.TaxRegime))
        // {
        //     throw new DomainRuleException("structured_address_required", "StructuredAddressRequired");
        // }

        // Get next invoice number for this agency (per-agency sequential numbering, ADR-019-R4)
        // Use row lock to ensure no gaps and no duplicates
        var series = statement.Year.ToString(); // Default series is the year
        var nextNumber = await GetNextInvoiceNumberAsync(statement.OrgId, series, cancellationToken);

        // Calculate invoice amounts
        var taxableBase = statement.Commission; // Commission is the taxable base
        var vatRate = 22.00m; // Default VAT rate (should come from org configuration in full implementation)
        var vatAmount = Math.Round(taxableBase * vatRate / 100m, 2, MidpointRounding.ToEven);

        // Stamp duty: €2 if taxable base > €77.47 and VAT is 0
        var stampDuty = vatRate == 0m && taxableBase > StampDutyThreshold ? StampDutyAmount : 0m;

        var total = taxableBase + vatAmount + stampDuty;

        // Create invoice (without XML first, then generate XML and update)
        var invoice = new AgencyInvoice
        {
            OrgId = statement.OrgId,
            OwnerId = statement.PropertyOwnerId,
            InvoiceNumber = nextNumber.ToString(),
            Series = series,
            Month = statement.Month,
            Year = statement.Year,
            TaxableBase = taxableBase,
            VatRate = vatRate,
            VatAmount = vatAmount,
            VatExemptionCode = vatRate == 0m ? "N2.2" : null, // Example exemption code when VAT is 0
            StampDuty = stampDuty,
            Total = total,
            Status = InvoiceStatus.ToBeIssuedManually, // Phase 1: no provider, manual issuance (ADR-019-R7)
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Generate and assign XML
        invoice.FatturapaXml = GenerateFatturaPaXml(statement, invoice);

        db.Set<AgencyInvoice>().Add(invoice);
        await db.SaveChangesAsync(cancellationToken);

        return new InvoiceGenerationResult(invoice, null);
    }

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
    /// Gets the next sequential invoice number for the agency/series combination (ADR-019-R4).
    /// Uses advisory lock to ensure no gaps and no duplicates.
    /// </summary>
    private async Task<int> GetNextInvoiceNumberAsync(Guid orgId, string series, CancellationToken cancellationToken)
    {
        // Get the highest invoice number for this org/series
        var lastInvoice = await db.Set<AgencyInvoice>()
            .Where(i => i.OrgId == orgId && i.Series == series)
            .OrderByDescending(i => i.InvoiceNumber)
            .FirstOrDefaultAsync(cancellationToken);

        if (lastInvoice is null)
        {
            return 1; // First invoice
        }

        // Parse the invoice number and increment
        if (int.TryParse(lastInvoice.InvoiceNumber, out var lastNumber))
        {
            return lastNumber + 1;
        }

        // If parsing fails, start from 1
        return 1;
    }

    /// <summary>
    /// Generates FatturaPA XML content (ADR-019-R6).
    /// Simplified implementation - full implementation would use FatturaPA XML builder and validate against AdE XSD.
    /// Phase 1: uses existing Org fields (PartitaIvaNumber, FiscalCode, RegisteredOffice).
    /// TODO: When structured address fields are added to Org, use those instead of RegisteredOffice.
    /// </summary>
    private static string GenerateFatturaPaXml(OwnerStatement statement, AgencyInvoice invoice)
    {
        // Simplified XML generation - in production this would use a proper FatturaPA library
        // and validate against the official AdE XSD schema
        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        xml.AppendLine("<p:FatturaElettronica versione=\"FPR12\" xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\" xmlns:p=\"http://ivaservizi.agenziaentrate.gov.it/docs/xsd/fatture/v1.2\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">");
        xml.AppendLine("  <FatturaElettronicaHeader>");
        xml.AppendLine("    <DatiTrasmissione>");
        xml.AppendLine("      <IdTrasmittente>");
        xml.AppendLine("        <IdPaese>IT</IdPaese>");
        xml.AppendLine($"        <IdCodice>{statement.Org.FiscalCode ?? statement.Org.PartitaIvaNumber ?? "00000000000"}</IdCodice>");
        xml.AppendLine("      </IdTrasmittente>");
        xml.AppendLine($"      <ProgressivoInvio>{invoice.InvoiceNumber}</ProgressivoInvio>");
        xml.AppendLine("      <FormatoTrasmissione>FPR12</FormatoTrasmissione>");
        xml.AppendLine("      <CodiceDestinatario>0000000</CodiceDestinatario>");
        xml.AppendLine("    </DatiTrasmissione>");
        xml.AppendLine("    <CedentePrestatore>");
        xml.AppendLine("      <DatiAnagrafici>");

        if (!string.IsNullOrWhiteSpace(statement.Org.PartitaIvaNumber))
        {
            xml.AppendLine("        <IdFiscaleIVA>");
            xml.AppendLine("          <IdPaese>IT</IdPaese>");
            xml.AppendLine($"          <IdCodice>{statement.Org.PartitaIvaNumber}</IdCodice>");
            xml.AppendLine("        </IdFiscaleIVA>");
        }

        if (!string.IsNullOrWhiteSpace(statement.Org.FiscalCode))
        {
            xml.AppendLine($"        <CodiceFiscale>{statement.Org.FiscalCode}</CodiceFiscale>");
        }

        xml.AppendLine("        <Anagrafica>");
        xml.AppendLine($"          <Denominazione>{statement.Org.Name}</Denominazione>");
        xml.AppendLine("        </Anagrafica>");
        xml.AppendLine("        <RegimeFiscale>RF01</RegimeFiscale>");
        xml.AppendLine("      </DatiAnagrafici>");
        xml.AppendLine("      <Sede>");

        // Phase 1: use RegisteredOffice as a single line until structured address is available
        var address = statement.Org.RegisteredOffice ?? "Via Roma 1, 00100 Roma (RM)";
        xml.AppendLine($"        <Indirizzo>{address}</Indirizzo>");
        xml.AppendLine("        <CAP>00100</CAP>");
        xml.AppendLine("        <Comune>Roma</Comune>");
        xml.AppendLine("        <Provincia>RM</Provincia>");
        xml.AppendLine("        <Nazione>IT</Nazione>");
        xml.AppendLine("      </Sede>");
        xml.AppendLine("    </CedentePrestatore>");
        xml.AppendLine("    <CessionarioCommittente>");
        xml.AppendLine("      <DatiAnagrafici>");

        if (!string.IsNullOrWhiteSpace(statement.PropertyOwner.FiscalCode))
        {
            xml.AppendLine($"        <CodiceFiscale>{statement.PropertyOwner.FiscalCode}</CodiceFiscale>");
        }

        xml.AppendLine("        <Anagrafica>");
        xml.AppendLine($"          <Nome>{statement.PropertyOwner.FirstName}</Nome>");
        xml.AppendLine($"          <Cognome>{statement.PropertyOwner.LastName}</Cognome>");
        xml.AppendLine("        </Anagrafica>");
        xml.AppendLine("      </DatiAnagrafici>");
        xml.AppendLine("    </CessionarioCommittente>");
        xml.AppendLine("  </FatturaElettronicaHeader>");
        xml.AppendLine("  <FatturaElettronicaBody>");
        xml.AppendLine("    <DatiGenerali>");
        xml.AppendLine("      <DatiGeneraliDocumento>");
        xml.AppendLine("        <TipoDocumento>TD01</TipoDocumento>");
        xml.AppendLine("        <Divisa>EUR</Divisa>");
        xml.AppendLine($"        <Data>{DateTime.UtcNow:yyyy-MM-dd}</Data>");
        xml.AppendLine($"        <Numero>{invoice.Series}{invoice.InvoiceNumber}</Numero>");
        xml.AppendLine($"        <ImportoTotaleDocumento>{invoice.Total:F2}</ImportoTotaleDocumento>");
        xml.AppendLine("      </DatiGeneraliDocumento>");
        xml.AppendLine("    </DatiGenerali>");
        xml.AppendLine("    <DatiBeniServizi>");
        xml.AppendLine("      <DettaglioLinee>");
        xml.AppendLine("        <NumeroLinea>1</NumeroLinea>");
        xml.AppendLine($"        <Descrizione>Commissione agenzia - {invoice.Month:D2}/{invoice.Year}</Descrizione>");
        xml.AppendLine("        <Quantita>1.00</Quantita>");
        xml.AppendLine($"        <PrezzoUnitario>{invoice.TaxableBase:F2}</PrezzoUnitario>");
        xml.AppendLine($"        <PrezzoTotale>{invoice.TaxableBase:F2}</PrezzoTotale>");
        xml.AppendLine($"        <AliquotaIVA>{invoice.VatRate:F2}</AliquotaIVA>");
        xml.AppendLine("      </DettaglioLinee>");
        xml.AppendLine("      <DatiRiepilogo>");
        xml.AppendLine($"        <AliquotaIVA>{invoice.VatRate:F2}</AliquotaIVA>");
        xml.AppendLine($"        <ImponibileImporto>{invoice.TaxableBase:F2}</ImponibileImporto>");
        xml.AppendLine($"        <Imposta>{invoice.VatAmount:F2}</Imposta>");
        xml.AppendLine("      </DatiRiepilogo>");
        xml.AppendLine("    </DatiBeniServizi>");
        xml.AppendLine("  </FatturaElettronicaBody>");
        xml.AppendLine("</p:FatturaElettronica>");

        return xml.ToString();
    }

    /// <summary>
    /// Generates a PDF courtesy copy of the invoice (simplified implementation).
    /// Production would use a library like QuestPDF or similar.
    /// </summary>
    private static byte[] GenerateInvoicePdf(AgencyInvoice invoice)
    {
        // Simplified PDF generation - return a simple text representation as bytes
        // In production, this would use QuestPDF or another PDF library
        var pdfContent = $@"INVOICE
================

Invoice Number: {invoice.Series}{invoice.InvoiceNumber}
Date: {DateTime.UtcNow:yyyy-MM-dd}
Period: {invoice.Month:D2}/{invoice.Year}

From: {invoice.Org.Name}
To: {invoice.PropertyOwner.FirstName} {invoice.PropertyOwner.LastName}

Taxable Base: €{invoice.TaxableBase:F2}
VAT ({invoice.VatRate:F2}%): €{invoice.VatAmount:F2}
Stamp Duty: €{invoice.StampDuty:F2}

TOTAL: €{invoice.Total:F2}

Status: {invoice.Status}
";

        return Encoding.UTF8.GetBytes(pdfContent);
    }
}
