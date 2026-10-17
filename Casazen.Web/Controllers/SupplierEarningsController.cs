using System.Text;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Supplier;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The supplier's earnings (SP-16a): the page "Incassi". Its completed and paid jobs with gross, commission and net, the figures above them (what is still
/// to be collected, what was paid this month, the commission, the late payments, eight months of net paid, the commission that applies now) and the same
/// rows as a CSV for its accountant. Every route is scoped to the caller's own supplier org (its supplier link, never a value of the request, and never
/// provisioned by a read of the money). <b>Not behind the flag <c>SupplierOnlinePayments</c></b> (with it off the list holds only jobs paid outside
/// CasaZen; the console hides the page). The answers carry money: nothing is cached by the browser or a proxy. Runbook <c>docs/runbooks/suppliers.md</c>
/// section 30.
/// </summary>
/// <remarks>
/// Errors: 404 <c>not_found</c> when the caller has no linked supplier org; 400 <c>validation_error</c> for a <c>tab</c> that is not one of the five or a
/// <c>from</c> after <c>to</c>; 422 <c>supplier_earnings_export_too_large</c> for an export of more than 20000 rows.
/// </remarks>
[ApiController]
[Route("api/supplier/earnings")]
[Authorize(Policy = CasazenPolicies.Supplier)]
public class SupplierEarningsController(
    ISupplierEarningsService earnings,
    ISupplierOrgContextResolver supplierOrgContextResolver,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// A page of the supplier's completed and paid jobs with their money, the newest first, and the figures above the list (<c>summary</c>, independent
    /// of the filters). <c>tab</c>: <c>tutti</c> (default), <c>da-incassare</c> (to collect, late and under review), <c>pagati</c> (paid, and refunded
    /// afterwards), <c>in-ritardo</c> (late) or <c>da-verificare</c> (under review by CasaZen); the tabs may overlap. <c>from</c>/<c>to</c>: Europe/Rome
    /// days (<c>YYYY-MM-DD</c>, both included) on the date of the row (the day it was paid for a paid job, otherwise the day it was completed).
    /// <c>clientId</c>: one customer (the host org or the private customer of the showcase, as in the inbox). <c>pageSize</c> up to 100. Amounts in cents;
    /// each row carries its <c>state</c> (an open set), <c>grossCents</c>, <c>commissionPercent</c>, <c>commissionCents</c> and <c>netCents</c> (empty
    /// where they do not exist yet, never 0), <c>paidAt</c>, <c>paidVia</c> and <c>refundedCents</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(SupplierEarningsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierEarningsResponse>> List(
        [FromQuery] string? tab = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, SupplierEarningsLimits.MaxPageSize);
        if (!TryReadQuery(tab, from, to, clientId, page, pageSize, out var query, out var refused))
            return refused;

        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var result = await earnings.GetAsync(orgId.Value, query, cancellationToken);

        SetNoStore();
        return Ok(SupplierEarningsMapper.ToResponse(result, page, pageSize));
    }

    /// <summary>
    /// The same rows as <c>GET api/supplier/earnings</c>, with the same filters (the page is ignored), as a CSV file for the supplier's accountant: UTF-8
    /// with a byte order mark, comma, CRLF, money in integer cents, days in Rome, one line per job. It holds the service and the customer's name as the
    /// console shows them and <b>no contact</b> (no e-mail, no phone: decision SP-D25); no VAT is computed (decision D4). A text that a spreadsheet would
    /// read as a formula gets a leading apostrophe. <c>Cache-Control: private, no-store</c>. 422 <c>supplier_earnings_export_too_large</c> above 20000 rows.
    /// </summary>
    [HttpGet("export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Export(
        [FromQuery] string? tab = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null,
        [FromQuery] Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadQuery(tab, from, to, clientId, 1, SupplierEarningsLimits.MaxPageSize, out var query, out var refused))
            return refused;

        var orgId = await supplierOrgContextResolver.GetLinkedSupplierOrgIdAsync(cancellationToken);
        if (orgId is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "SupplierProfileNotFound");

        var rows = await earnings.ExportAsync(orgId.Value, query, cancellationToken);

        // UTF-8 with a byte order mark: a spreadsheet opens the accents of a name right (as the staff export of the commission).
        var csv = Encoding.UTF8.GetBytes(SupplierEarningsCsv.Write(rows));
        var body = new byte[Encoding.UTF8.Preamble.Length + csv.Length];
        Encoding.UTF8.Preamble.CopyTo(body);
        csv.CopyTo(body, Encoding.UTF8.Preamble.Length);

        SetNoStore();
        return File(body, SupplierEarningsCsv.ContentType + "; charset=utf-8", SupplierEarningsCsv.FileName(_clock.TodayInRomeAsDateOnly()));
    }

    /// <summary>The filters of the list, or the 400 that refuses them.</summary>
    private bool TryReadQuery(
        string? tab,
        DateOnly? from,
        DateOnly? to,
        Guid? clientId,
        int page,
        int pageSize,
        out SupplierEarningsQuery query,
        out ActionResult refused)
    {
        query = new SupplierEarningsQuery(SupplierEarningsTab.All, from, to, clientId, page, pageSize);
        refused = null!;

        if (!SupplierEarningsTabs.TryParse(tab, out var parsedTab))
        {
            refused = this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, SupplierEarningsErrors.TabInvalidMessageKey);
            return false;
        }

        if (from is { } start && to is { } end && start > end)
        {
            refused = this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, SupplierEarningsErrors.PeriodInvalidMessageKey);
            return false;
        }

        query = query with { Tab = parsedTab };
        return true;
    }

    /// <summary>The answers describe money: nothing is cached by the browser or a proxy.</summary>
    private void SetNoStore() => Response.Headers.CacheControl = "private, no-store";
}
