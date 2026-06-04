using System.Security.Claims;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

public sealed record ContextPermissionRequirement(string ContextKey, string PermissionKey) : IAuthorizationRequirement;

public class ContextAuthorizationHandler(
    IContextAuthorizationService contextAuthorizationService) : AuthorizationHandler<ContextPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ContextPermissionRequirement requirement)
    {
        var userId = context.User.FindFirstValue("sub")
            ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");

        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var authorized = await contextAuthorizationService.HasPermissionAsync(
            userId,
            requirement.ContextKey,
            requirement.PermissionKey);

        if (authorized)
        {
            context.Succeed(requirement);
        }
    }
}
