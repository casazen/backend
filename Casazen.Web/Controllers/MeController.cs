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
    /// <summary>
    /// The contexts the caller may enter, the <c>account</c> context of the org team (AM-01) included. The list is sorted by
    /// key, so <c>account</c> comes first: a client that has no area for a context must leave it out (the web app does, in
    /// <c>WorkspaceProvider</c>) or it becomes the active one. The authorization does not depend on this list: the
    /// memberships and the policies decide.
    /// </summary>
    [HttpGet("contexts")]
    public async Task<ActionResult<UserContextsResponse>> GetContexts(CancellationToken cancellationToken)
    {
        var sub = GetSub();
        if (string.IsNullOrWhiteSpace(sub))
        {
            return Unauthorized();
        }

        var user = await GetCurrentUserAsync(sub);
        if (!user.IsActive)
        {
            // Normally refused earlier by InactiveAccountMiddleware (PL-03); same answer if the account was deactivated meanwhile.
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.AccountInactive, "AccountInactive");
        }

        var contexts = await contextAuthorizationService.GetUserContextsAsync(sub, cancellationToken);

        // UI-13a: the last used context is told only while the caller can still enter it (an access taken away): a stale key
        // would make the client open an area that refuses it.
        var lastUsed = contexts.FirstOrDefault(c => string.Equals(c.ContextKey, user.LastUsedContextKey, StringComparison.OrdinalIgnoreCase));

        var response = new UserContextsResponse(
            sub,
            contexts.Select(c => new ContextBootstrapDto(
                c.ContextKey,
                c.DisplayName,
                c.RoleKey,
                c.Permissions,
                c.DefaultRoute)).ToList(),
            lastUsed?.ContextKey);

        return Ok(response);
    }

    /// <summary>
    /// Remembers the area the caller entered last (UI-13a, F13 of the redesign): <c>GET /api/me/contexts</c> gives it back as
    /// <c>lastUsedContextKey</c>, so the client opens the same area on any device (before, only the browser remembered it).
    /// It must be one of the contexts the caller can enter right now, as that list tells them: anything else, a key that does
    /// not exist or an access the caller does not have, is a 422 <c>context_not_accessible</c> and nothing is stored. The key
    /// is stored as the list spells it (the match ignores case). It writes one column of the caller's own row.
    /// </summary>
    /// <response code="200">Stored (or already the stored value): the key as stored.</response>
    /// <response code="400"><c>validation_error</c>: no key, or longer than the column.</response>
    /// <response code="422"><c>context_not_accessible</c>.</response>
    [HttpPut("last-context")]
    [ProducesResponseType(typeof(LastContextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<LastContextResponse>> PutLastContext(
        [FromBody] SetLastContextRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var sub = GetSub();
        if (string.IsNullOrWhiteSpace(sub))
            return Unauthorized();

        var user = await GetCurrentUserAsync(sub);
        if (!user.IsActive)
            return this.ApiProblem(StatusCodes.Status403Forbidden, ProblemCodes.AccountInactive, "AccountInactive");

        var wanted = request.ContextKey!.Trim();
        var context = (await contextAuthorizationService.GetUserContextsAsync(sub, cancellationToken))
            .FirstOrDefault(c => string.Equals(c.ContextKey, wanted, StringComparison.OrdinalIgnoreCase));
        if (context is null)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                LastContextErrors.ContextNotAccessible,
                LastContextErrors.ContextNotAccessibleMessageKey);
        }

        await userService.SetLastUsedContextAsync(sub, context.ContextKey, cancellationToken);
        return Ok(new LastContextResponse(context.ContextKey));
    }

    /// <summary>The caller's row, created from the token when it is the first request (the claims fill what the row lacks).</summary>
    private Task<Casazen.Core.Entities.User> GetCurrentUserAsync(string sub)
    {
        var (email, firstName, lastName) = AccountEmailResolver.ReadProfile(User);

        return userService.GetCurrentUserAsync(sub, email, firstName, lastName);
    }

    private string? GetSub() =>
        User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
}
