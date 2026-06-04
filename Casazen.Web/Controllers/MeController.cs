using System.Security.Claims;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Auth;
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
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "User account inactive" });
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
