namespace Casazen.Core.Services;

/// <summary>
/// The agency-to-owner invoices of the retired internal generator (ADR-019 amended, E06/F01/US02/T05), read-only: the stored rows stay readable (their XML
/// and courtesy copy) and are marked <c>legacy_unverified</c>. The invoices of the property manager to the owners are made since E14/F01/US01/T01 by
/// <see cref="IOwnerInvoiceService"/> as fiscal documents (<c>POST api/agency-invoices/generate</c> makes the drafts of a period); none is written here.
/// </summary>
public interface IAgencyInvoiceService
{
    /// <summary>
    /// Retrieves the FatturaPA XML content of an invoice (ADR-019-R6).
    /// </summary>
    /// <param name="invoiceId">ID of the invoice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>XML string validated against AdE XSD, or null if invoice is still Draft.</returns>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
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
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// 404 invoice_not_found: invoice does not exist or not in caller's org.
    /// 422 invoice_not_finalized: invoice is still in Draft status (cannot generate PDF).
    /// </exception>
    Task<byte[]> GetInvoicePdfAsync(Guid invoiceId, CancellationToken cancellationToken = default);
}
