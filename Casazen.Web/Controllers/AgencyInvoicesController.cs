using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The historical agency invoices for property owners (ADR-019 amended, E06/F01/US02/T05): their generation is retired (410); the stored XML and
/// the courtesy copy stay readable, marked <c>legacy_unverified</c>. Every action is scoped to the caller's agency org: an invoice of another org
/// answers 404.
/// </summary>
[ApiController]
[Route("api/agency-invoices")]
[Authorize(Policy = CasazenPolicies.AgencyInvoiceManage)]
public class AgencyInvoicesController(
    IAgencyInvoiceService invoiceService,
    ILogger<AgencyInvoicesController> logger) : ControllerBase
{
    /// <summary>
    /// Retired (ADR-019 amended, E06/F01/US02/T05, AC-NF110-6): CasaZen issues no agency-to-owner invoice, fiscal number or FatturaPA XML. Always 410
    /// <c>owner_invoice_issuance_removed</c>; the agency links the invoice of its external fiscal system to the statement version
    /// (<c>POST api/owner-accounting/versions/{id}/external-documents</c>). The stored invoices stay readable below.
    /// </summary>
    /// <param name="request">Invoice generation request with statement ID.</param>
    /// <response code="410">owner_invoice_issuance_removed: the internal generation is retired.</response>
    [HttpPost("generate")]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<InvoiceGenerationDto>> GenerateInvoice(
        [FromBody] GenerateInvoiceRequest request)
    {
        logger.LogInformation(
            "Generating invoice for statement {StatementId}",
            request.StatementId);

        var result = await invoiceService.GenerateInvoiceAsync(
            request.StatementId,
            HttpContext.RequestAborted);

        return Ok(new InvoiceGenerationDto
        {
            Id = result.Invoice.Id,
            InvoiceNumber = result.Invoice.InvoiceNumber,
            Series = result.Invoice.Series,
            Month = result.Invoice.Month,
            Year = result.Invoice.Year,
            TaxableBase = result.Invoice.TaxableBase,
            VatRate = result.Invoice.VatRate,
            VatAmount = result.Invoice.VatAmount,
            StampDuty = result.Invoice.StampDuty,
            Total = result.Invoice.Total,
            Status = result.Invoice.Status.ToString(),
            CreatedAt = result.Invoice.CreatedAt,
            Warning = result.Warning
        });
    }

    /// <summary>
    /// Retrieves the FatturaPA XML content of an invoice (ADR-019).
    /// Returns XML validated against AdE XSD schema, ready for manual SDI submission.
    /// Requires <c>agency.invoice.manage</c> policy.
    /// </summary>
    /// <param name="id">Invoice ID.</param>
    /// <response code="200">XML content.</response>
    /// <response code="404">Invoice not found or not in caller's org.</response>
    /// <response code="422">Invoice not finalized (still in Draft status).</response>
    [HttpGet("{id:guid}/xml")]
    [Produces("application/xml")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<string>> GetInvoiceXml(Guid id)
    {
        logger.LogInformation("Retrieving XML for invoice {InvoiceId}", id);

        var xml = await invoiceService.GetInvoiceXmlAsync(id, HttpContext.RequestAborted);

        if (xml is null)
        {
            return NotFound();
        }

        return Content(xml, "application/xml");
    }

    /// <summary>
    /// Generates a PDF courtesy copy of the invoice (ADR-019, AC-NF20-6).
    /// Human-readable PDF for the property owner, not the official FatturaPA XML.
    /// Requires <c>agency.invoice.manage</c> policy.
    /// </summary>
    /// <param name="id">Invoice ID.</param>
    /// <response code="200">PDF byte array.</response>
    /// <response code="404">Invoice not found or not in caller's org.</response>
    /// <response code="422">Invoice not finalized (still in Draft status).</response>
    [HttpGet("{id:guid}/pdf")]
    [Produces("application/pdf")]
    [ProducesResponseType(typeof(byte[]), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<byte[]>> GetInvoicePdf(Guid id)
    {
        logger.LogInformation("Generating PDF for invoice {InvoiceId}", id);

        var pdf = await invoiceService.GetInvoicePdfAsync(id, HttpContext.RequestAborted);

        return File(pdf, "application/pdf", $"invoice-{id}.pdf");
    }
}

/// <summary>Request to generate an invoice from a statement.</summary>
public sealed record GenerateInvoiceRequest
{
    /// <summary>ID of the approved owner statement to invoice.</summary>
    public Guid StatementId { get; init; }
}

/// <summary>Result of invoice generation.</summary>
public sealed record InvoiceGenerationDto
{
    public Guid Id { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public string Series { get; init; } = string.Empty;
    public int Month { get; init; }
    public int Year { get; init; }
    public decimal TaxableBase { get; init; }
    public decimal VatRate { get; init; }
    public decimal VatAmount { get; init; }
    public decimal StampDuty { get; init; }
    public decimal Total { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public string? Warning { get; init; }
}
