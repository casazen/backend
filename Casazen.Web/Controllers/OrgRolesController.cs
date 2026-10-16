using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The matrix «chi può fare cosa» of the org roles (AM-02c): for each of the five roles what it holds in each context and how
/// much of each of the twelve capabilities that is, so the team screens draw the roles, the comparison between them and the
/// «Cosa vede questo utente» preview from the same permissions the API enforces (a reading of the seeded roles, not a second
/// table). Any active member of the org may read it (it describes roles, not people); behind the <c>OrgTeam</c> flag: 404 while
/// it is off, before the authentication. Runbook <c>docs/runbooks/org-team.md</c> part 6.
/// </summary>
[ApiController]
[Route("api/orgs/me/roles")]
[FeatureGate(FeatureFlags.OrgTeam)]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class OrgRolesController(
    IOrgRoleMatrixService matrix,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// The five roles, owner first. <c>assignable</c> is about the caller: the owner may give the administrator role, an
    /// administrator may not, nobody gives the owner role, and a member who manages no people gives none. <c>scopeSupported</c>
    /// is true for the collaborator only. 403 when the caller is not an active member of the org.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(OrgRolesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgRolesResponse>> Get(CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        return Ok(OrgRolesResponse.From(await matrix.GetAsync(caller.OrgId, caller.UserId, cancellationToken)));
    }
}
