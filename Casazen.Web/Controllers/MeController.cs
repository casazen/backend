using System.Security.Claims;
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
    IContextAuthorizationService contextAuthorizationService) : ControllerBase
{
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
