using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>The caller of an org team endpoint (AM-02): its org and its account, or the answer to give when it has neither.</summary>
internal readonly record struct OrgTeamCaller(Guid OrgId, string UserId, ActionResult? Problem);

internal static class OrgTeamControllerExtensions
{
    /// <summary>
    /// The caller's org (<see cref="IOrgContextResolver"/>: the org of its own account, never a value of the request) and its
    /// account id. 404 when it has no org, 401 when there is no account (the policy of the endpoints already refused both,
    /// this is the guard of the action).
    /// </summary>
    public static async Task<OrgTeamCaller> ResolveOrgTeamCallerAsync(
        this ControllerBase controller,
        IOrgContextResolver orgContextResolver,
        CancellationToken cancellationToken)
    {
        var userId = controller.User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return new OrgTeamCaller(Guid.Empty, string.Empty, controller.Unauthorized());

        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
        {
            return new OrgTeamCaller(
                Guid.Empty,
                userId,
                controller.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NoOrganizationAssigned"));
        }

        return new OrgTeamCaller(orgId.Value, userId, null);
    }
}
