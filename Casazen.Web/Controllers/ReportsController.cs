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
/// same days. Each says its base, <c>competenza</c> (accrual) for the revenue and the channels. No guest data, no PDF, no comparison with
/// the period before.
/// <para>
/// All three take <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, Europe/Rome days, both included, at most 24 months: 422
/// <c>report_period_too_long</c> beyond, 400 <c>report_invalid_period</c> when missing, out of order or out of the supported years),
/// an optional <c>propertyId</c> (one property of the caller's reach, 404 for any other) and <c>format</c> (<c>json</c>, the default, or
/// <c>csv</c>; 400 <c>report_invalid_format</c> for anything else).
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
    public const string InvalidPeriodCode = "report_invalid_period";

    /// <summary>Code of a period longer than <see cref="HostReportRules.MaxMonths"/> months.</summary>
    public const string PeriodTooLongCode = "report_period_too_long";

    /// <summary>Code of a <c>format</c> that is not <c>json</c> or <c>csv</c>.</summary>
    public const string InvalidFormatCode = "report_invalid_format";

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

    // The words of the request, before anything is read: the format, then the days. The problem to answer, or null with the query.
    private ObjectResult? Check(DateOnly? fromDay, DateOnly? toDay, Guid? propertyId, string? format, out HostReportQuery query, out bool csv)
    {
        query = new HostReportQuery(default, default, propertyId);
        csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        if (!csv && !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidFormatCode, "ReportInvalidFormat");

        if (fromDay is not { } first || toDay is not { } last)
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "ReportInvalidPeriod");

        switch (HostReportRules.CheckPeriod(first, last))
        {
            case HostReportPeriodCheck.Invalid:
                return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "ReportInvalidPeriod");
            case HostReportPeriodCheck.TooLong:
                return this.ApiProblem(StatusCodes.Status422UnprocessableEntity, PeriodTooLongCode, "ReportPeriodTooLong", HostReportRules.MaxMonths);
        }

        query = new HostReportQuery(first, last, propertyId);
        return null;
    }

    private async Task<HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
