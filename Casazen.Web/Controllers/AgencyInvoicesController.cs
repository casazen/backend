using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The agency invoices for property owners (ADR-019 amended, E06/F01/US02/T05, E14/F01/US01/T01): <c>generate</c> makes the drafts of the owner invoices of a
/// period (fiscal documents made from the statement versions, behind <c>Features:OwnerInvoicing</c>); the historical rows of the retired generator stay readable,
/// marked <c>legacy_unverified</c>. Every action is scoped to the caller's agency org: an invoice of another org answers 404.
/// </summary>
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
    /// <summary>
    /// The drafts of the owner invoices of a period (<c>{ year, month }</c>), one for every owner with a statement version of it that is not superseded: made, or
    /// made again, with the counts (owners, made, made again, issued already, nothing to invoice) and the owners whose draft could not be made with the code of
    /// the reason. 404 while <c>Features:OwnerInvoicing</c> is off; 403 for a caller restricted to some properties; 422 <c>owner_period_invalid</c>,
    /// <c>issuer_profile_incomplete</c>, <c>document_rule_not_configured</c> (nothing is written).
    /// </summary>
    /// <param name="request">The period.</param>
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
