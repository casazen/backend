using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/orgs/me/roles")]
[Authorize(Policy = CasazenPolicies.Authenticated)]
public class OrgRolesController(
    IOrgRoleMatrixService matrix,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
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
