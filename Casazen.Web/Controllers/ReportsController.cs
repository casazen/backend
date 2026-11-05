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
/// The reports of short rent (SR-10): the revenue, the occupancy and the stays of the Home cut by property or by channel, as JSON or
/// as a CSV to download. They are about money, so they need <c>payment.read</c> (SR-D7: the collaborator never receives an aggregated
/// amount, whatever the page does), and they show only what the caller's <see cref="HostScope"/> reaches, in SQL. The numbers are the
/// Home's, worked out by the same code (<see cref="IHostDashboardService"/>): the total of a report is the figure the Home gives for the
/// same days. Each says its base, <c>competenza</c> (accrual) for the revenue and the channels. No guest data, no PDF. The first three
/// have no comparison with the period before; <c>performance</c> and <c>trend</c> (NF-96) add ADR, RevPAR and the same calendar days one
/// year earlier.
/// <para>
/// All of them take <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included, at most 24 months: 422
/// <c>report_period_too_long</c> beyond, 400 <c>report_invalid_period</c> when missing, out of order or out of the supported years),
/// an optional <c>propertyId</c> (one property of the caller's reach, 404 for any other) and <c>format</c> (<c>json</c>, the default, or
/// <c>csv</c>; 400 <c>report_invalid_format</c> for anything else). <c>performance</c> and <c>trend</c> also take <c>groupBy</c>
/// (<c>none</c>, <c>property</c>, <c>channel</c>; 400 <c>report_invalid_group_by</c>) and <c>compare</c> (<c>none</c>,
/// <c>previousYear</c>; 400 <c>report_invalid_compare</c>), and <c>trend</c> a <c>bucket</c> (<c>auto</c>, <c>week</c>, <c>month</c>;
/// 400 <c>report_invalid_bucket</c>).
/// </para>
/// </summary>
[ApiController]
[Route("api/reports")]
[Authorize(Policy = CasazenPolicies.PaymentRead)]
public class ReportsController(
    IHostDashboardService reports,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>Code of a <c>from</c> and <c>to</c> that are missing, out of order or out of the supported years.</summary>
    public const string InvalidPeriodCode = ReportRequestChecks.InvalidPeriodCode;

    /// <summary>Code of a period longer than <see cref="HostReportRules.MaxMonths"/> months.</summary>
    public const string PeriodTooLongCode = ReportRequestChecks.PeriodTooLongCode;

    /// <summary>Code of a <c>format</c> that is not <c>json</c> or <c>csv</c>.</summary>
    public const string InvalidFormatCode = ReportRequestChecks.InvalidFormatCode;

    /// <summary>Code of a <c>groupBy</c> that is not <c>none</c>, <c>property</c> or <c>channel</c>.</summary>
    public const string InvalidGroupByCode = ReportRequestChecks.InvalidGroupByCode;

    /// <summary>Code of a <c>compare</c> that is not <c>none</c> or <c>previousYear</c>.</summary>
    public const string InvalidCompareCode = ReportRequestChecks.InvalidCompareCode;

    /// <summary>Code of a <c>bucket</c> that is not <c>auto</c>, <c>week</c> or <c>month</c>.</summary>
    public const string InvalidBucketCode = ReportRequestChecks.InvalidBucketCode;

    /// <summary>
    /// Revenue by property, by accrual (<c>competenza</c>): per property of the scope with stays, the confirmed stays pro rata per night
    /// in the period (<see cref="StayKpiRules"/>, the Home's rule) with their count and nights, and the total.
    /// </summary>
    [HttpGet("revenue-by-property")]
    [ProducesResponseType(typeof(RevenueByPropertyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RevenueByProperty(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (Check(fromDay, toDay, propertyId, format, out var query, out var csv) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetRevenueByPropertyAsync(scope, query, cancellationToken);
        return csv
            ? File(HostReportDocuments.Csv(report), "text/csv", HostReportDocuments.FileName("ricavo-per-immobile-competenza", report.Period))
            : Ok(RevenueByPropertyReportDto.From(report, HostReportDocuments.RevenueByPropertyTitle));
    }

    /// <summary>
    /// Occupancy by property: per active short-rent property of the scope, the nights occupied over the nights available as the Home
    /// counts them (<see cref="PropertyOccupancy"/>), and the total.
    /// </summary>
    [HttpGet("occupancy-by-property")]
    [ProducesResponseType(typeof(OccupancyByPropertyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> OccupancyByProperty(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (Check(fromDay, toDay, propertyId, format, out var query, out var csv) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetOccupancyByPropertyAsync(scope, query, cancellationToken);
        return csv
            ? File(HostReportDocuments.Csv(report), "text/csv", HostReportDocuments.FileName("occupazione-per-immobile", report.Period))
            : Ok(OccupancyByPropertyReportDto.From(report, HostReportDocuments.OccupancyByPropertyTitle));
    }

    /// <summary>
    /// Bookings by channel, by accrual (<c>competenza</c>): per booking source with confirmed stays in the period, their count, nights and
    /// revenue (the Home's rule) and the share of all the stays, and the total.
    /// </summary>
    [HttpGet("bookings-by-channel")]
    [ProducesResponseType(typeof(BookingsByChannelReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> BookingsByChannel(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (Check(fromDay, toDay, propertyId, format, out var query, out var csv) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetBookingsByChannelAsync(scope, query, cancellationToken);
        return csv
            ? File(HostReportDocuments.Csv(report), "text/csv", HostReportDocuments.FileName("prenotazioni-per-canale-competenza", report.Period))
            : Ok(BookingsByChannelReportDto.From(report, HostReportDocuments.BookingsByChannelTitle));
    }

    /// <summary>
    /// ADR, RevPAR and the occupancy of the Home (NF-96) for the whole scope, per property (<c>groupBy=property</c>) or per channel
    /// (<c>groupBy=channel</c>), with <c>compare=previousYear</c> next to the same calendar days one year earlier and the differences,
    /// worked out here. ADR is the revenue of the confirmed stays over their nights, RevPAR the same revenue over the available nights
    /// (both null, never zero, when the denominator is zero); the total is the figure the Home gives for the same days.
    /// </summary>
    [HttpGet("performance")]
    [ProducesResponseType(typeof(PerformanceReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Performance(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? groupBy,
        [FromQuery] string? compare,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (this.CheckPerformanceRequest(fromDay, toDay, groupBy, compare, null, format, true, out var request) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetPerformanceAsync(
            scope,
            new HostPerformanceQuery(request.From, request.To, propertyId, request.GroupBy, request.CompareYear),
            cancellationToken);
        return request.Csv
            ? File(
                HostReportDocuments.Csv(report, request.GroupBy),
                "text/csv",
                HostReportDocuments.FileName(HostReportDocuments.PerformanceFileName(request.GroupBy, false), report.Period))
            : Ok(PerformanceReportDto.From(report, request.GroupBy, HostReportDocuments.PerformanceTitle(request.GroupBy)));
    }

    /// <summary>
    /// The same figures week by week or month by month (NF-96): one line for the whole scope, or one for each property or channel (at
    /// most 25, the rest summed in an <c>other</c> one), each bucket next to its days one year earlier with <c>compare=previousYear</c>.
    /// <c>bucket=auto</c> cuts by week up to 92 days and by month beyond.
    /// </summary>
    [HttpGet("trend")]
    [ProducesResponseType(typeof(TrendReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Trend(
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        [FromQuery] string? groupBy,
        [FromQuery] string? compare,
        [FromQuery] string? bucket,
        [FromQuery] string? format,
        CancellationToken cancellationToken)
    {
        if (this.CheckPerformanceRequest(fromDay, toDay, groupBy, compare, bucket, format, true, out var request) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetTrendAsync(
            scope,
            new HostTrendQuery(request.From, request.To, propertyId, request.GroupBy, request.CompareYear, null, request.Bucket),
            cancellationToken);
        return request.Csv
            ? File(
                HostReportDocuments.Csv(report, request.GroupBy),
                "text/csv",
                HostReportDocuments.FileName(HostReportDocuments.PerformanceFileName(request.GroupBy, true), report.Period))
            : Ok(TrendReportDto.From(report, request.GroupBy, HostReportDocuments.TrendTitle(request.GroupBy)));
    }

    // The words of the request, before anything is read: the format, then the days. The problem to answer, or null with the query.
    private ObjectResult? Check(DateOnly? fromDay, DateOnly? toDay, Guid? propertyId, string? format, out HostReportQuery query, out bool csv)
    {
        query = new HostReportQuery(default, default, propertyId);
        if (this.CheckFormat(format, out csv) is { } formatProblem)
            return formatProblem;

        if (this.CheckPeriod(fromDay, toDay, out var first, out var last) is { } periodProblem)
            return periodProblem;

        query = new HostReportQuery(first, last, propertyId);
        return null;
    }

    private async Task<HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
