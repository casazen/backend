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
/// as a CSV or an Excel file to download. They are about money, so they need <c>payment.read</c> (SR-D7: the collaborator never receives an aggregated
/// amount, whatever the page does), and they show only what the caller's <see cref="HostScope"/> reaches, in SQL. The numbers are the
/// Home's, worked out by the same code (<see cref="IHostDashboardService"/>): the total of a report is the figure the Home gives for the
/// same days. Each says its base, <c>competenza</c> (accrual) for the revenue and the channels. No guest data, no PDF. The first three
/// have no comparison with the period before; <c>performance</c> and <c>trend</c> (NF-96) add ADR, RevPAR and the same calendar days one
/// year earlier.
/// <para>
/// All of them take <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included, at most 24 months: 422
/// <c>report_period_too_long</c> beyond, 400 <c>report_invalid_period</c> when missing, out of order or out of the supported years),
/// an optional <c>propertyId</c> (one property of the caller's reach, 404 for any other) and <c>format</c> (<c>json</c>, the default,
/// <c>csv</c> or <c>xlsx</c>, the Excel file with the same columns and numbers as the CSV (NF-101); 400 <c>report_invalid_format</c> for
/// anything else). <c>performance</c> and <c>trend</c> also take <c>groupBy</c>
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
    public const string InvalidPeriodCode = ReportRequestChecks.InvalidPeriodCode;

    public const string PeriodTooLongCode = ReportRequestChecks.PeriodTooLongCode;

    public const string InvalidFormatCode = ReportRequestChecks.InvalidFormatCode;

    public const string InvalidGroupByCode = ReportRequestChecks.InvalidGroupByCode;

    public const string InvalidCompareCode = ReportRequestChecks.InvalidCompareCode;

    public const string InvalidBucketCode = ReportRequestChecks.InvalidBucketCode;

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
        if (Check(fromDay, toDay, propertyId, format, out var query, out var fileFormat) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetRevenueByPropertyAsync(scope, query, cancellationToken);
        return fileFormat == ReportFormat.Json
            ? Ok(RevenueByPropertyReportDto.From(report, HostReportDocuments.RevenueByPropertyTitle))
            : this.ReportFile(
                fileFormat,
                fileFormat == ReportFormat.Xlsx ? HostReportDocuments.Xlsx(report) : HostReportDocuments.Csv(report),
                HostReportDocuments.FileName("ricavo-per-immobile-competenza", report.Period, ReportRequestChecks.FileExtensionOf(fileFormat)));
    }

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
        if (Check(fromDay, toDay, propertyId, format, out var query, out var fileFormat) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetOccupancyByPropertyAsync(scope, query, cancellationToken);
        return fileFormat == ReportFormat.Json
            ? Ok(OccupancyByPropertyReportDto.From(report, HostReportDocuments.OccupancyByPropertyTitle))
            : this.ReportFile(
                fileFormat,
                fileFormat == ReportFormat.Xlsx ? HostReportDocuments.Xlsx(report) : HostReportDocuments.Csv(report),
                HostReportDocuments.FileName("occupazione-per-immobile", report.Period, ReportRequestChecks.FileExtensionOf(fileFormat)));
    }

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
        if (Check(fromDay, toDay, propertyId, format, out var query, out var fileFormat) is { } problem)
            return problem;
        if (await GetScopeAsync(cancellationToken) is not { } scope)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var report = await reports.GetBookingsByChannelAsync(scope, query, cancellationToken);
        return fileFormat == ReportFormat.Json
            ? Ok(BookingsByChannelReportDto.From(report, HostReportDocuments.BookingsByChannelTitle))
            : this.ReportFile(
                fileFormat,
                fileFormat == ReportFormat.Xlsx ? HostReportDocuments.Xlsx(report) : HostReportDocuments.Csv(report),
                HostReportDocuments.FileName("prenotazioni-per-canale-competenza", report.Period, ReportRequestChecks.FileExtensionOf(fileFormat)));
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
        return request.Format == ReportFormat.Json
            ? Ok(PerformanceReportDto.From(report, request.GroupBy, HostReportDocuments.PerformanceTitle(request.GroupBy)))
            : this.ReportFile(
                request.Format,
                request.Format == ReportFormat.Xlsx
                    ? HostReportDocuments.Xlsx(report, request.GroupBy)
                    : HostReportDocuments.Csv(report, request.GroupBy),
                HostReportDocuments.FileName(
                    HostReportDocuments.PerformanceFileName(request.GroupBy, false),
                    report.Period,
                    ReportRequestChecks.FileExtensionOf(request.Format)));
    }

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
        return request.Format == ReportFormat.Json
            ? Ok(TrendReportDto.From(report, request.GroupBy, HostReportDocuments.TrendTitle(request.GroupBy)))
            : this.ReportFile(
                request.Format,
                request.Format == ReportFormat.Xlsx
                    ? HostReportDocuments.Xlsx(report, request.GroupBy)
                    : HostReportDocuments.Csv(report, request.GroupBy),
                HostReportDocuments.FileName(
                    HostReportDocuments.PerformanceFileName(request.GroupBy, true),
                    report.Period,
                    ReportRequestChecks.FileExtensionOf(request.Format)));
    }

    private ObjectResult? Check(
        DateOnly? fromDay, DateOnly? toDay, Guid? propertyId, string? format, out HostReportQuery query, out ReportFormat fileFormat)
    {
        query = new HostReportQuery(default, default, propertyId);
        if (this.CheckFormat(format, out fileFormat) is { } formatProblem)
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
