using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// Host dashboard (PC-16, A2-29): KPIs computed on the server for a period, the host's day (SR-03) and the state of the iCal
/// import feeds. Every read is limited to the caller's org and, unless the role is org-wide, to the properties they reach
/// (<see cref="Casazen.Core.Authorization.HostScope"/>, TN-3), filtered in SQL.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class DashboardController(
    IHostDashboardService dashboardService,
    IHostTodayService todayService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>Code of a <c>period</c> or <c>month</c> query value that is not accepted.</summary>
    public const string InvalidPeriodCode = "dashboard_invalid_period";

    /// <summary>First and last year a month may be asked for.</summary>
    private const int MinYear = 2000;
    private const int MaxYear = 2100;

    /// <summary>
    /// KPIs of the period: occupancy (occupied / available nights), revenue, today's arrivals and departures (Europe/Rome),
    /// upcoming check-ins and the last bookings. <c>period=Month</c> (default) with an optional <c>month=yyyy-MM</c> (the
    /// current month when missing), <c>period=Last30Days</c> (today included) or <c>period=Next30Days</c> (today included).
    /// 400 <c>dashboard_invalid_period</c>. SR-03, all optional: <c>propertyId</c> narrows everything to one property of the
    /// caller's reach (404 for any other), <c>compare=true</c> adds the figures of the period before (<c>previous</c>), and the
    /// money collected (<c>collected</c>) is there only for a caller who may read payments.
    /// </summary>
    [HttpGet("kpis")]
    [ProducesResponseType(typeof(HostDashboardKpisDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostDashboardKpisDto>> GetKpis(
        [FromQuery] string? period,
        [FromQuery] string? month,
        [FromQuery] Guid? propertyId,
        [FromQuery] bool compare,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(period, month, out var kind, out var monthStart))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "DashboardInvalidPeriod");

        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var query = new HostDashboardQuery(kind, monthStart, propertyId, compare, await CanReadPaymentsAsync());
        var kpis = await dashboardService.GetKpisAsync(scope, query, cancellationToken);
        return Ok(HostDashboardKpisDto.From(kpis));
    }

    /// <summary>
    /// The numbers of the Home in one read (SR-03b): the figures of <c>kpis</c> (same code) for the period and for the one before,
    /// the difference between them worked out here (<c>occupancy.deltaPoints</c> in percentage points, <c>revenue.deltaPercent</c>),
    /// the stays that arrive in the period and in the one before with the requests waiting for the host (<c>upcoming</c>), the next
    /// three arrivals from today (<c>nextArrivals</c>) and the revenue by ISO week or by month, which adds up to the revenue
    /// (<c>series</c>). <c>period=Month</c> (default, with an optional <c>month=yyyy-MM</c>), <c>Last30Days</c>, <c>Next30Days</c> or
    /// <c>Custom</c> with <c>from</c> and <c>to</c> (<c>yyyy-MM-dd</c>, both included, at most 366 days); 400
    /// <c>dashboard_invalid_period</c>. <c>propertyId</c> narrows everything to one property of the caller's reach (404 for any other).
    /// Money is payment data (SR-D7): without <c>payment.read</c> the answer has no <c>revenue</c>, <c>collected</c> or <c>series</c>
    /// (nor the same inside <c>previous</c>) at all.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(HostOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<HostOverviewDto>> GetOverview(
        [FromQuery] string? period,
        [FromQuery] string? month,
        [FromQuery(Name = "from")] DateOnly? fromDay,
        [FromQuery(Name = "to")] DateOnly? toDay,
        [FromQuery] Guid? propertyId,
        CancellationToken cancellationToken)
    {
        if (!TryParseOverviewPeriod(period, month, fromDay, toDay, out var kind, out var monthStart))
            return this.ApiProblem(StatusCodes.Status400BadRequest, InvalidPeriodCode, "DashboardInvalidOverviewPeriod");

        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var overview = await dashboardService.GetOverviewAsync(
            scope,
            new HostOverviewQuery(kind, monthStart, fromDay, toDay, propertyId, await CanReadPaymentsAsync()),
            cancellationToken);
        return Ok(HostOverviewDto.From(overview));
    }

    /// <summary>
    /// The host's day in one read (SR-03): today's arrivals and departures (Europe/Rome) with how far their online check-in is, the
    /// check-ins to come, the "pay at the property" requests waiting with the time left to answer, and the things to do, most
    /// pressing first, each with what it lacks and where it leads (a key, never a path). The stays whose payment failed are
    /// listed only for a caller who may read payments.
    /// </summary>
    [HttpGet("today")]
    [ProducesResponseType(typeof(HostTodayDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<HostTodayDto>> GetToday(CancellationToken cancellationToken)
    {
        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var today = await todayService.GetTodayAsync(scope, new HostTodayOptions(await CanReadPaymentsAsync()), cancellationToken);
        return Ok(HostTodayDto.From(today));
    }

    /// <summary>
    /// The iCal import feeds of the caller's properties (PC-11): last sync, status and the translated error of each,
    /// feeds with an error first. Never the import URL.
    /// </summary>
    [HttpGet("ical-feeds")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(IReadOnlyList<HostDashboardIcalFeedDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<HostDashboardIcalFeedDto>>> GetIcalFeeds(
        [FromServices] IStringLocalizer<SharedResources> localizer,
        CancellationToken cancellationToken)
    {
        var scope = await GetScopeAsync(cancellationToken);
        if (scope is null)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.Forbidden, "Forbidden");

        var feeds = await dashboardService.GetIcalFeedsAsync(scope, cancellationToken);
        return Ok(feeds.Select(feed =>
        {
            var (code, message) = ICalErrorMessages.Describe(feed.LastError, localizer);
            return new HostDashboardIcalFeedDto
            {
                FeedId = feed.FeedId,
                PropertyId = feed.PropertyId,
                PropertyName = feed.PropertyName,
                Channel = feed.Channel.ToString(),
                Label = feed.Label,
                LastImportAt = feed.LastImportAt,
                LastImportStatus = feed.LastImportStatus?.ToString(),
                LastErrorCode = code,
                LastError = message,
            };
        }).ToList());
    }

    private async Task<Casazen.Core.Authorization.HostScope?> GetScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    // Money (collected, failed payments) is payment data: it needs payment.read, which the collaborator does not hold (AM-03). The
    // permission of the role, as the policy of PaymentsController asks it; which payments are then counted is the scope's, in SQL.
    private async Task<bool> CanReadPaymentsAsync() =>
        (await authorizationService.AuthorizeAsync(User, CasazenPolicies.PaymentRead)).Succeeded;

    /// <summary>
    /// <c>period</c>: <c>Month</c> (default), <c>Last30Days</c> or <c>Next30Days</c>, case-insensitive; <c>month</c>:
    /// <c>yyyy-MM</c>, only with <c>Month</c>.
    /// </summary>
    public static bool TryParsePeriod(string? period, string? month, out HostDashboardPeriodKind kind, out DateOnly? monthStart)
    {
        monthStart = null;

        // A free period has days to say, which kpis does not take: it is as unknown a name there as before it existed.
        return TryParseKind(period, out kind) && kind != HostDashboardPeriodKind.Custom && TryParseMonth(kind, month, out monthStart);
    }

    /// <summary>
    /// <c>period</c> of the overview (SR-03b): the periods of <see cref="TryParsePeriod"/> and <c>Custom</c>, which needs <c>from</c>
    /// and <c>to</c> (both days, in order, at most <see cref="HostDashboardPeriod.MaxCustomDays"/> days, in the supported years) and
    /// takes no <c>month</c>; the others take no <c>from</c> or <c>to</c>.
    /// </summary>
    public static bool TryParseOverviewPeriod(
        string? period,
        string? month,
        DateOnly? from,
        DateOnly? to,
        out HostDashboardPeriodKind kind,
        out DateOnly? monthStart)
    {
        monthStart = null;
        if (!TryParseKind(period, out kind))
            return false;

        if (kind == HostDashboardPeriodKind.Custom)
        {
            return string.IsNullOrWhiteSpace(month)
                && from is { } first
                && to is { } last
                && HostDashboardPeriod.TryCustom(first, last, out _);
        }

        return from is null && to is null && TryParseMonth(kind, month, out monthStart);
    }

    // Names only: a number would bind to an undefined enum value.
    private static bool TryParseKind(string? period, out HostDashboardPeriodKind kind)
    {
        kind = HostDashboardPeriodKind.Month;
        if (string.IsNullOrWhiteSpace(period))
            return true;

        return Enum.TryParse(period.Trim(), ignoreCase: true, out kind)
            && Enum.IsDefined(kind)
            && !period.Trim().All(char.IsAsciiDigit);
    }

    private static bool TryParseMonth(HostDashboardPeriodKind kind, string? month, out DateOnly? monthStart)
    {
        monthStart = null;
        if (string.IsNullOrWhiteSpace(month))
            return true;

        if (kind != HostDashboardPeriodKind.Month
            || !DateOnly.TryParseExact(month.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Year is < MinYear or > MaxYear)
            return false;

        monthStart = parsed;
        return true;
    }
}
