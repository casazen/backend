using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Result of invoice generation (ADR-019).
/// </summary>
/// <param name="Invoice">The generated invoice entity.</param>
/// <param name="Warning">Non-fatal warning (e.g., missing structured address, would require manual entry).</param>
public sealed record InvoiceGenerationResult(AgencyInvoice Invoice, string? Warning = null);

/// <summary>
/// The agency-to-owner invoices of the retired internal generator (ADR-019 amended, E06/F01/US02/T05): no new invoice is made; the stored ones stay
/// readable (their XML and courtesy copy) and are marked <c>legacy_unverified</c>.
/// </summary>
public interface IAgencyInvoiceService
{
    /// <summary>
    /// Retired: CasaZen issues no agency-to-owner invoice, fiscal number or FatturaPA XML (AC-NF110-6). Always 410 <c>owner_invoice_issuance_removed</c>;
    /// the agency links the invoice of its external fiscal system to the statement version instead.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainGoneException">Always.</exception>
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
