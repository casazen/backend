using System.Globalization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// A member who cannot do something asks the administrators of its org for access (AM-02b). Any active member may ask, whatever
/// its role, so the endpoint needs only a signed-in account; the service checks that the account is an active member of the
/// org. Behind the <c>OrgTeam</c> flag: 404 while it is off. Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
[ApiController]
[Route("api/orgs/me/access-requests")]
[FeatureGate(FeatureFlags.OrgTeam)]
public class OrgAccessRequestsController(
    IOrgAccessRequestService requests,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Tells the owner and the administrators of the org, by email in the language of the request, that the caller asks for
    /// access to an area or a page, with a short note if it wrote one. Nothing is stored but a line of the activity log
    /// (who, which area; never the note). Rate limited per person (429), and at most three requests a day for each person
    /// (409 <c>access_request_limit_reached</c>). 403 when the caller is not an active member of the org, 422
    /// <c>access_request_area_unknown</c> for an area or page that cannot be asked for.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.Authenticated)]
    [EnableRateLimiting(RateLimitPolicies.OrgAccessRequest)]
    [ProducesResponseType(typeof(OrgAccessRequestedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<OrgAccessRequestedDto>> Create(
        [FromBody] RequestOrgAccessRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var requested = await requests.RequestAsync(
            new RequestOrgAccess(
                caller.OrgId,
                caller.UserId,
                request.Area,
                request.Note,
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName),
            cancellationToken);

        return Accepted(OrgAccessRequestedDto.From(requested));
    }
}
