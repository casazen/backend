using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The overview of the org for the home of the account (AM-02c): the standing of the plan, the seats, the properties, the
/// invitations, the requests for access, the trusted suppliers, the Alloggiati credentials and the latest activity of people, in
/// one answer, so the page does not compose six requests and repeat the permissions of each. For the owner and the
/// administrators (policy <see cref="CasazenPolicies.OrgMembersManage"/>); a section whose own permission the caller lacks is
/// left out. Behind the <c>OrgTeam</c> flag: 404 while it is off, before the authentication. Runbook
/// <c>docs/runbooks/org-team.md</c> part 6.
/// </summary>
[ApiController]
[Route("api/orgs/me/overview")]
[FeatureGate(FeatureFlags.OrgTeam)]
[Authorize(Policy = CasazenPolicies.OrgMembersManage)]
public class OrgOverviewController(
    IOrgOverviewService overview,
    IContextAuthorizationService contextAuthorization,
    IHostScopeResolver hostScopeResolver,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// The figures of the home. The properties are counted inside what the caller reaches. 403 when the caller reaches nothing of
    /// the org's host data (a deactivated member), 404 when it has no org.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(OrgOverviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgOverviewResponse>> Get(CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        if (await hostScopeResolver.ResolveHostScopeAsync(User, caller.OrgId, cancellationToken) is not { } scope)
            return Forbid();

        // The sections the caller may see: each one is behind its own permission of the account context.
        var permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in OrgOverviewSections.All)
        {
            if (await contextAuthorization.HasPermissionAsync(caller.UserId, AccountContext.Key, permission, cancellationToken))
                permissions.Add(permission);
        }

        return Ok(OrgOverviewResponse.From(await overview.GetAsync(caller.OrgId, scope, permissions, cancellationToken)));
    }
}
