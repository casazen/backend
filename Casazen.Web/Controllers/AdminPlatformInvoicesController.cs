using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Admin;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// E-invoicing queue of the CasaZen SaaS invoices (PL-13, A1-08), for the platform admin. Without an SDI provider every
/// paid invoice is <c>manual_required</c>: the admin issues the e-invoice by hand (runbook billing-tax.md) and records
/// it here. Invoices flagged by <c>taxReviewReason</c> need a check before issuing.
/// </summary>
[ApiController]
[Route("api/admin/platform-invoices")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminPlatformInvoicesController(IPlatformInvoiceService platformInvoices) : ControllerBase
{
    private const int MaxPageSize = 100;

    /// <summary>
    /// Invoices of every org, newest first. <paramref name="sdiStatus"/>: one of <c>manual_required</c>, <c>pending</c>,
    /// <c>submitted</c>, <c>failed</c>, <c>manual_issued</c> (400 otherwise); <paramref name="taxReview"/>=true keeps only
    /// the invoices that need a tax check.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<PlatformInvoiceAdminDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResultDto<PlatformInvoiceAdminDto>>> List(
        [FromQuery] string? sdiStatus = null,
        [FromQuery] bool? taxReview = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(sdiStatus) && !PlatformInvoiceSdiStatuses.All.Contains(sdiStatus))
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "PlatformInvoiceSdiStatusUnknown");

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var result = await platformInvoices.ListAsync(sdiStatus, taxReview, page, pageSize, cancellationToken);
        return Ok(new PagedResultDto<PlatformInvoiceAdminDto>
        {
            Items = result.Items.Select(PlatformInvoiceAdminDto.From).ToList(),
            TotalCount = result.TotalCount,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// Records that the e-invoice was issued by hand, with its reference. 404 for an unknown invoice, 409
    /// <c>platform_invoice_sdi_already_issued</c> when it was already submitted or issued.
    /// </summary>
    [HttpPost("{id:guid}/sdi-manual-issued")]
    [ProducesResponseType(typeof(PlatformInvoiceAdminDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PlatformInvoiceAdminDto>> MarkSdiIssuedManually(
        Guid id,
        [FromBody] MarkSdiIssuedManuallyRequest request,
        CancellationToken cancellationToken)
    {
        var reference = request.Reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length > 255)
            return this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "PlatformInvoiceSdiReferenceInvalid");

        var invoice = await platformInvoices.MarkSdiIssuedManuallyAsync(id, reference, cancellationToken);
        return Ok(PlatformInvoiceAdminDto.From(invoice));
    }
}
