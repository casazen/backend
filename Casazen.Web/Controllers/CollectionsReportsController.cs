using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The collections report of the host (NF-97): the money that came in by date, by payment method and by channel, what is still to collect and what reached
/// the account, as JSON or as a CSV to download. It is money, so it needs <c>payment.read</c> like the other reports (SR-D7), and it shows only what the
/// caller's <see cref="HostScope"/> reaches, in SQL. The total of the collections is the Home's "incassato" for the same days (<see cref="IHostCollectionsReportService"/>).
/// No guest data and no PDF.
/// <para>
/// <c>collections</c> takes <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included, at most 24 months: 422 <c>report_period_too_long</c>
/// beyond, 400 <c>report_invalid_period</c> when missing, out of order or out of the supported years), an optional <c>propertyId</c> (one property of the
/// caller's reach, 404 for any other) and <c>format</c> (<c>json</c>, the default, or <c>csv</c>; 400 <c>report_invalid_format</c> for anything else).
/// <c>balances-to-collect</c> is read as of today and takes <c>propertyId</c> and <c>format</c> only.
/// </para>
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class CollectionsReportsController(
    IHostCollectionsReportService reports,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// The money collected between two days, by cash and net of refunds: per day (up to 92 days) or month, per payment method and per channel, with the total
    /// the Home gives for the same days, and the credits of the card provider and of the channels (<c>available: false</c> where the figures are not stored).
    /// </summary>
    [HttpGet("collections")]
    [ProducesResponseType(typeof(CollectionsReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Collections(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (this.CheckFormat(format, out var csv) is { } formatProblem)
            return formatProblem;
        if (this.CheckPeriod(fromDay, toDay, out var first, out var last) is { } periodProblem)
            return periodProblem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetCollectionsAsync(scope, new HostReportQuery(first, last, propertyId), cancellationToken);
        return csv
            ? File(HostCollectionsReportDocuments.Csv(report), "text/csv", HostReportDocuments.FileName("incassato-per-cassa", report.Period))
            : Ok(CollectionsReportDto.From(report, HostCollectionsReportDocuments.CollectionsTitle));
    }

    /// <summary>
    /// The stays that still have something to collect, as of today: not cancelled, not an expired checkout hold, not ended more than 24 months ago, with the amount due above what was collected;
    /// the total for each currency. Never a guest name or contact.
    /// </summary>
    [HttpGet("balances-to-collect")]
    [ProducesResponseType(typeof(BalancesToCollectReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BalancesToCollect(
        [FromQuery] Guid? propertyId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (this.CheckFormat(format, out var csv) is { } formatProblem)
            return formatProblem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetBalancesToCollectAsync(scope, new HostBalancesQuery(propertyId), cancellationToken);
        return csv
            ? File(HostCollectionsReportDocuments.Csv(report), "text/csv", HostCollectionsReportDocuments.BalancesFileName(report.AsOf))
            : Ok(BalancesToCollectReportDto.From(report, HostCollectionsReportDocuments.BalancesTitle));
    }

    private async Task<HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
