using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The agenda and the overview of the long-term area (LR-01, B2): what is due and what waits, across all the leases the caller
/// reaches. Reads only, <c>lease.read</c>, restricted in SQL to the caller's org and, unless org-wide, to the properties they
/// reach (<see cref="HostScope"/>, TN-3 and AM-03: the scope the lease endpoints use). Dates are Europe/Rome calendar days.
/// </summary>
[ApiController]
[Route("api/long-rent")]
[Authorize(Policy = CasazenPolicies.LeaseRead)]
public class LongRentAgendaController(
    ILongRentAgendaService agenda,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    TimeProvider? timeProvider = null) : ControllerBase
{
    private const int FirstYear = 2000;
    private const int LastYear = 2100;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// The deadlines from <c>from</c> to <c>to</c> (both <c>yyyy-MM-dd</c>, included; default today and 90 days later, at most
    /// 366 days), optionally of one <c>type</c> (<c>RliRegistration</c>, <c>Questura</c>, <c>LeaseEnd</c>, <c>Notice</c> or
    /// <c>Rent</c>), in date order. Only what is still to be done: a registration, a Questura communication or a rent installment
    /// already done is not listed. A window that contains today also carries what is past and still open, flagged
    /// <c>isOverdue</c>. The last day of notice is six months before the end of a 4+4 or 3+2 contract (a transitory has none). IMU
    /// and ISTAT are not listed. <c>truncated</c>: more installments were due than the answer carries (the earliest 500).
    /// 400 for a window or a type that is not valid.
    /// </summary>
    [HttpGet("deadlines")]
    [ProducesResponseType(typeof(LongRentDeadlines), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LongRentDeadlines>> GetDeadlines(
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string? type = null,
        CancellationToken cancellationToken = default)
    {
        var today = _clock.TodayInRomeAsDateOnly();
        var start = today;
        if (!string.IsNullOrWhiteSpace(from) && !TryParseDay(from, out start))
            return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentAgendaErrorCodes.RangeInvalid, "LongRentDeadlinesRangeInvalid");

        var end = start.AddDays(LongRentDeadlineRules.DefaultWindowDays);
        if (!string.IsNullOrWhiteSpace(to) && !TryParseDay(to, out end))
            return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentAgendaErrorCodes.RangeInvalid, "LongRentDeadlinesRangeInvalid");

        if (end < start || end.DayNumber - start.DayNumber > LongRentDeadlineRules.MaxWindowDays)
            return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentAgendaErrorCodes.RangeInvalid, "LongRentDeadlinesRangeInvalid");

        LongRentDeadlineType? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!EnumNames.TryParseDefined<LongRentDeadlineType>(type, out var parsed))
                return this.ApiProblem(StatusCodes.Status400BadRequest, LongRentAgendaErrorCodes.TypeUnknown, "LongRentDeadlinesTypeUnknown");
            typeFilter = parsed;
        }

        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        return Ok(await agenda.GetDeadlinesAsync(scope, new LongRentDeadlinesQuery(start, end, typeFilter), cancellationToken));
    }

    /// <summary>
    /// The overview of the area on today's date: the leases counted by where they stand (as the views of <c>GET api/leases</c>
    /// say), the rent of the current month (expected, collected, still to come, overdue), the checklist (rent overdue,
    /// leases to register, Questura communications, leases to sign: only what has something, most urgent first, each with the
    /// date and the lease to open) and the next deadline that is not past, within 90 days.
    /// </summary>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(LongRentOverview), StatusCodes.Status200OK)]
    public async Task<ActionResult<LongRentOverview>> GetOverview(CancellationToken cancellationToken = default)
    {
        var scope = await GetHostScopeAsync(cancellationToken);
        if (scope is null)
            return Unauthorized();

        return Ok(await agenda.GetOverviewAsync(scope, cancellationToken));
    }

    /// <summary><c>yyyy-MM-dd</c> between the years 2000 and 2100.</summary>
    private static bool TryParseDay(string value, out DateOnly day)
    {
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)
            && day.Year is >= FirstYear and <= LastYear)
        {
            return true;
        }

        day = default;
        return false;
    }

    private async Task<HostScope?> GetHostScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }
}
