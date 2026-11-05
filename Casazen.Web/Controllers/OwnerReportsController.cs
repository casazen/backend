using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The performance of an agency's properties cut by property owner (NF-96, <c>GET /api/reports/by-owner</c>): occupancy, revenue, ADR
/// and RevPAR of each owner, weighted by the share it holds of each property in the days of the report, the properties nobody owns in a
/// row of their own, and the total the Home gives. As JSON or as a CSV to download, by accrual (<c>competenza</c>), the base said in the
/// title.
/// <para>
/// It shows money per owner like the statements do, so it needs <b>both</b> policies: <c>agency.statement.view</c> (the owner and the
/// administrators of an org on the agency plan; the property manager, who holds only <c>agency.owner.view</c>, does not pass, and an
/// org that is not on the agency plan answers 403 like the other agency endpoints) and <c>payment.read</c> (aggregated money, SR-D7).
/// It reads only what the caller's <see cref="HostScope"/> reaches, in SQL, and takes <c>from</c>, <c>to</c> and <c>format</c> with the
/// checks and the error codes of <see cref="ReportsController"/>. Nothing about a guest and nothing personal about an owner but its
/// name: never a fiscal code, an IBAN, an e-mail or a phone.
/// </para>
/// </summary>
[ApiController]
[Route("api/reports/by-owner")]
[Authorize(Policy = CasazenPolicies.AgencyStatementView)]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class OwnerReportsController(
    IOwnerReportService ownerReports,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    /// <summary>
    /// The report by owner for the days from <c>from</c> to <c>to</c> (<c>yyyy-MM-dd</c>, both included, at most 24 months): a row for each
    /// owner that holds a part of a property in them, the row of the properties nobody owns, and the total of the Home.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(OwnerReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Get(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (this.CheckFormat(format, out var csv) is { } formatProblem)
            return formatProblem;
        if (this.CheckPeriod(fromDay, toDay, out var first, out var last) is { } periodProblem)
            return periodProblem;

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await ownerReports.GetByOwnerAsync(scope, new HostPerformanceQuery(first, last), cancellationToken);
        return csv
            ? File(OwnerReportDocuments.Csv(report), "text/csv", OwnerReportDocuments.FileName(report.Period))
            : Ok(OwnerReportDto.From(report, OwnerReportDocuments.Title, localizer["OwnerAnonymizedLabel"].Value));
    }
}
