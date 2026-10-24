using Casazen.Core.Authorization;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The fiscal report of the long-term area (LR-11b), per lease, for the accountant: the rent accrued in a year, the rent collected
/// according to the rent register and, for a lease in cedolare secca, the estimated tax. It is an <b>estimate</b> and every number
/// declares its base (accrual or cash). Reads only, <c>lease.read</c> like the rest of the area, restricted in SQL to the caller's org
/// and, unless org-wide, to the properties they reach (<see cref="HostScope"/>, TN-3 and AM-03). The file carries the tenant's name
/// (D29) and nothing else about a person; nothing about its content is logged and the answers are not cached.
/// </summary>
[ApiController]
[Route("api/long-rent/fiscal")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LongRentFiscalController(
    ILongRentFiscalReportService report,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The report of <c>year</c> (default the current year in Rome; from 2000 to the current year, else 400
    /// <c>long_rent_fiscal_year_invalid</c>): a row per lease signed by every party that is in force in some month of the year, judged on
    /// the end of the period it is in now, with its accrual (whole months times the monthly rent), the rent collected in the year
    /// according to the register (<c>null</c> when the register holds nothing of the lease), the regime and the estimated cedolare secca;
    /// and the totals. A lease in the ordinary regime has no figure: the tax depends on the landlord's total income.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(LongRentFiscalReport), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get([FromQuery] int? year = null, CancellationToken cancellationToken = default)
    {
        if (CheckYear(year) is not { } checkedYear)
            return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentFiscalErrorCodes.YearInvalid, "LongRentFiscalYearInvalid");

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        NoStore();
        return Ok(await report.GetAsync(scope, checkedYear, cancellationToken));
    }

    /// <summary>
    /// The same report as a CSV to download (<c>format=csv</c>, the default; any other 400 <c>long_rent_fiscal_format_invalid</c>):
    /// one row per lease and the total, with «Stima da validare con il commercialista» on its first row and the day it was made.
    /// </summary>
    [HttpGet("export")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Export(
        [FromQuery] int? year = null, [FromQuery] string? format = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(format) && !string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentFiscalErrorCodes.FormatInvalid, "LongRentFiscalFormatInvalid");
        if (CheckYear(year) is not { } checkedYear)
            return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentFiscalErrorCodes.YearInvalid, "LongRentFiscalYearInvalid");

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        var fiscal = await report.GetAsync(scope, checkedYear, cancellationToken);
        NoStore();
        return File(LongRentFiscalDocuments.Csv(fiscal), "text/csv", LongRentFiscalDocuments.FileName(checkedYear));
    }

    // The year asked for, or the current one in Rome; null when it is not between the first year and the current one.
    private int? CheckYear(int? year)
    {
        var current = _clock.TodayInRomeAsDateOnly().Year;
        var asked = year ?? current;
        return asked is >= LongRentFiscalRules.FirstYear && asked <= current ? asked : null;
    }

    // Names of tenants and money of a landlord: neither a browser nor a proxy keeps a copy.
    private void NoStore() => Response.Headers.CacheControl = "private, no-store";

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
