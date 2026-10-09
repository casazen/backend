using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Auth;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/me")]
[Authorize]
public class MeController(
    IUserService userService,
    IContextAuthorizationService contextAuthorizationService,
    IFeatureFlags featureFlags) : ControllerBase
{
    /// <summary>
    /// The contexts the caller may enter. The <c>account</c> context (AM-01) is listed only with the
    /// <see cref="FeatureFlags.OrgTeam"/> flag on: the web app of today does not know it (its workspace switcher fails on a
    /// context key it has no icon for, and the first one in the list becomes the active context), so listing it would
    /// break the app of every owner. The authorization does not depend on this list: the memberships and the policies
    /// work with the flag off. Turn the flag on together with the account screens (AM-04).
    /// </summary>
    [HttpGet("contexts")]
    public async Task<ActionResult<UserContextsResponse>> GetContexts(CancellationToken cancellationToken)
    {
        var sub = GetSub();
        if (string.IsNullOrWhiteSpace(sub))
        {
            return Unauthorized();
        }

        var email = User.FindFirst("email")?.Value
                    ?? User.FindFirst(ClaimTypes.Email)?.Value
                    ?? string.Empty;
        var firstName = User.FindFirst("given_name")?.Value
                        ?? User.FindFirst("name")?.Value?.Split(' ').FirstOrDefault()
                        ?? string.Empty;
        var lastName = User.FindFirst("family_name")?.Value
                       ?? User.FindFirst("name")?.Value?.Split(' ').Skip(1).FirstOrDefault()
                       ?? string.Empty;

        var user = await userService.GetCurrentUserAsync(sub, email, firstName, lastName);
        if (!user.IsActive)
        {
            // Normally refused earlier by InactiveAccountMiddleware (PL-03); same answer if the flag changed meanwhile.
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.AccountInactive, "AccountInactive");
        }

        var contexts = await contextAuthorizationService.GetUserContextsAsync(sub, cancellationToken);
        if (!featureFlags.IsEnabled(FeatureFlags.OrgTeam))
            contexts = contexts.Where(c => !AccountContext.IsAccountContext(c.ContextKey)).ToList();

        var response = new UserContextsResponse(
            sub,
            contexts.Select(c => new ContextBootstrapDto(
                c.ContextKey,
                c.DisplayName,
                c.RoleKey,
                c.Permissions,
                c.DefaultRoute)).ToList(),
            user.LastUsedContextKey);

        return Ok(response);
    }

    private string? GetSub() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
}
