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
/// The channel costs report of the host (NF-98): what each channel costs, gross, commission and net, the processing fees of the direct stays and how each channel
/// settles its commission, as JSON or as a CSV to download. It is money, so it needs <c>payment.read</c> like the other reports (SR-D7), and it shows only what the
/// caller's <see cref="HostScope"/> reaches, in SQL. By accrual, like the revenue of the Home: the stays are the ones of <c>bookings-by-channel</c>. A cost nobody
/// reported is null in the answer (never zero) and the stays without one are counted apart. No guest data, no PDF. The rows come ordered by cost rate, the highest
/// first, and the sentence "how much each channel costs" is written by the client from <c>costRate</c> and <c>commission</c>.
/// <para>
/// It takes <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included, at most 24 months: 422 <c>report_period_too_long</c> beyond, 400
/// <c>report_invalid_period</c> when missing, out of order or out of the supported years), an optional <c>propertyId</c> (one property of the caller's reach, 404 for
/// any other) and <c>format</c> (<c>json</c>, the default, or <c>csv</c>; 400 <c>report_invalid_format</c> for anything else).
/// </para>
/// </summary>
[ApiController]
[Route("api/reports/channel-costs")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class ChannelCostsReportsController(
    IChannelCostsReportService reports,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// Gross, commission and net for each channel with a stay in the period, the cost rate, how the commission is settled and the stays without a reported cost, the
    /// highest cost rate first, with the total.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(ChannelCostsReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChannelCosts(
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

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetChannelCostsAsync(scope, new HostReportQuery(first, last, propertyId), cancellationToken);
        return csv
            ? File(ChannelCostsReportDocuments.Csv(report), "text/csv", ChannelCostsReportDocuments.FileName(report.Period))
            : Ok(ChannelCostsReportDto.From(report, ChannelCostsReportDocuments.Title));
    }
}
