using System.Globalization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The activity log of the caller's org (AM-02b): who did what, in a short list with ids and codes and no personal data, and
/// the same list as a CSV. For the people who hold <c>org.activity.read</c> (the owner and the administrators, policy
/// <see cref="CasazenPolicies.OrgActivityRead"/>); behind the <c>OrgTeam</c> flag: 404 while it is off. The org is always
/// the caller's own (read from its account), never a value of the request. Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
[ApiController]
[Route("api/orgs/me")]
[FeatureGate(FeatureFlags.OrgTeam)]
[Authorize(Policy = CasazenPolicies.OrgActivityRead)]
public class OrgActivityController(
    IOrgActivityService activity,
    IOrgContextResolver orgContextResolver,
    TimeProvider timeProvider) : ControllerBase
{
    /// <summary>
    /// One page of the log, newest first, with the lines that match all the filters given: <c>from</c> and <c>to</c> (UTC,
    /// both included), <c>type</c> (one or more events, repeated or comma separated), <c>area</c>, <c>actor</c> (an account id,
    /// or <c>system</c> for what no person did). <c>page</c> from 1, <c>pageSize</c> 50 (100 at most). The text of a line is
    /// composed by the client from its type and its ids. 400 for an unknown event or area or for <c>from</c> after <c>to</c>.
    /// </summary>
    [HttpGet("activity")]
    [ProducesResponseType(typeof(PagedResultDto<OrgActivityEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PagedResultDto<OrgActivityEntryDto>>> List(
        [FromQuery] OrgActivityListQuery query,
        CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var filter = ToFilter(query, out var problem);
        if (problem is not null)
            return problem;

        var page = await activity.ListAsync(caller.OrgId, filter!, query.Page, query.PageSize, cancellationToken);
        return Ok(new PagedResultDto<OrgActivityEntryDto>
        {
            Items = page.Items.Select(OrgActivityEntryDto.From).ToList(),
            TotalCount = page.TotalCount,
            Page = page.Page,
            PageSize = page.PageSize,
        });
    }

    /// <summary>
    /// The whole matching log as a CSV, newest first, with the filters of the list (no paging). The header is fixed
    /// (<c>id,when,actor,area,type,subjectType,subjectId,details</c>), a log with nothing in it is the header alone, and no
    /// personal data is in any cell. Never cached.
    /// </summary>
    [HttpGet("activity.csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Export([FromQuery] OrgActivityListQuery query, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var filter = ToFilter(query, out var problem);
        if (problem is not null)
            return problem;

        var today = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return new OrgActivityCsvResult(
            activity.StreamAsync(caller.OrgId, filter!, cancellationToken),
            $"activity-{today}.csv");
    }

    /// <summary>The filter the query asks for, or the 400 that says which part of it is wrong.</summary>
    private OrgActivityFilter? ToFilter(OrgActivityListQuery query, out ActionResult? problem)
    {
        problem = null;

        var types = new List<OrgActivityType>();
        foreach (var name in (query.Type ?? []).SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            if (!OrgActivityCatalog.TryParseType(name, out var type))
            {
                problem = this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "OrgActivityTypeUnknown");
                return null;
            }

            types.Add(type);
        }

        OrgActivityArea? area = null;
        if (!string.IsNullOrWhiteSpace(query.Area))
        {
            if (!OrgActivityCatalog.TryParseArea(query.Area, out var parsed))
            {
                problem = this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "OrgActivityAreaUnknown");
                return null;
            }

            area = parsed;
        }

        if (query.From is { } from && query.To is { } to && from > to)
        {
            problem = this.ApiProblem(StatusCodes.Status400BadRequest, ProblemCodes.ValidationError, "OrgActivityRangeInvalid");
            return null;
        }

        var actor = query.Actor?.Trim();
        var system = string.Equals(actor, OrgActivityRules.SystemActor, StringComparison.OrdinalIgnoreCase);

        return new OrgActivityFilter(
            query.From,
            query.To,
            types.Count == 0 ? null : types,
            area,
            system || string.IsNullOrEmpty(actor) ? null : actor,
            system);
    }
}
