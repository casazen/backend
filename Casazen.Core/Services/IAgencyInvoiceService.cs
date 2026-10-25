using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Result of invoice generation (ADR-019).
/// </summary>
/// <param name="Invoice">The generated invoice entity.</param>
/// <param name="Warning">Non-fatal warning (e.g., missing structured address, would require manual entry).</param>
public sealed record InvoiceGenerationResult(AgencyInvoice Invoice, string? Warning = null);

/// <summary>
/// FatturaPA invoice generation service for agencies (ADR-019).
/// Generates invoices from monthly owner statements, validates against AdE XSD schema,
/// and produces XML/PDF for manual or automatic SDI submission.
/// </summary>
public interface IAgencyInvoiceService
{
    /// <summary>
    /// Generates a FatturaPA invoice from a monthly owner statement (ADR-019-R2).
    /// Validates single supplier per invoice (ADR-019-R5), assigns sequential invoice number
    /// with no gaps (ADR-019-R4), calculates VAT and stamp duty, generates and validates XML
    /// against official AdE schema (ADR-019-R6).
    /// </summary>
    /// <param name="statementId">ID of the approved statement to invoice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Invoice generation result with the created invoice and optional warning.</returns>
    /// <exception cref="DomainRuleException">
    /// 422 invoice_multiple_suppliers: statement lines reference multiple property owners (ADR-019-R5).
    /// 422 structured_address_required: agency org missing required structured address fields (ADR-019-R9).
    /// 422 statement_not_found: statement does not exist or not in caller's org.
    /// 422 statement_not_approved: statement status is not Approved (only approved statements can be invoiced).
    /// 422 invoice_already_exists: an invoice for this statement already exists.
    /// </exception>
    Task<InvoiceGenerationResult> GenerateInvoiceAsync(Guid statementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the FatturaPA XML content of an invoice (ADR-019-R6).
    /// </summary>
    /// <param name="invoiceId">ID of the invoice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML string validated against AdE XSD, or null if invoice is still Draft.</returns>
    /// <exception cref="DomainRuleException">
    /// 404 invoice_not_found: invoice does not exist or not in caller's org.
    /// 422 invoice_not_finalized: invoice is still in Draft status (XML not yet generated).
    /// </exception>
    Task<string?> GetInvoiceXmlAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates a PDF courtesy copy of the invoice (ADR-019-R10, AC-NF20-6).
    /// </summary>
    /// <param name="invoiceId">ID of the invoice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>PDF byte array.</returns>
    /// <exception cref="DomainRuleException">
    /// 404 invoice_not_found: invoice does not exist or not in caller's org.
    /// 422 invoice_not_finalized: invoice is still in Draft status (cannot generate PDF).
    /// </exception>
    Task<byte[]> GetInvoicePdfAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}
