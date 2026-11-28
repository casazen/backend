using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/agency-invoices")]
[Authorize(Policy = CasazenPolicies.AgencyInvoiceManage)]
public class AgencyInvoicesController(
    IAgencyInvoiceService invoiceService,
    IOwnerInvoiceService ownerInvoices,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    ILogger<AgencyInvoicesController> logger) : ControllerBase
{
    [HttpPost("generate")]
    [FeatureGate(FeatureFlags.OwnerInvoicing)]
    [ProducesResponseType(typeof(OwnerInvoiceGenerationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerInvoiceGenerationDto>> GenerateInvoice([FromBody] GenerateOwnerInvoicesRequest request)
    {
        var ct = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(ct);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, ct) is not { IsOrgWide: true })
            return Forbid();

        logger.LogInformation("Generating the owner invoice drafts of org {OrgId} for {Year}-{Month}", orgId, request.Year, request.Month);
        var generation = await ownerInvoices.CreateDraftsForPeriodAsync(orgId.Value, request.Year, request.Month, User.GetUserId(), ct);
        return Ok(OwnerInvoiceGenerationDto.From(generation));
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
