using System.Security.Claims;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Requirement of a context policy (<c>CasazenPolicies</c>): <see cref="PermissionKey"/> held in at least one of
/// <see cref="ContextKeys"/>. A permission counts only in the context that grants it.
/// </summary>
public sealed record ContextPermissionRequirement(IReadOnlyList<string> ContextKeys, string PermissionKey) : IAuthorizationRequirement
{
    /// <summary>A requirement on a single context.</summary>
    public ContextPermissionRequirement(string contextKey, string permissionKey)
        : this([contextKey], permissionKey)
    {
    }
}

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

        foreach (var contextKey in requirement.ContextKeys)
        {
            if (await contextAuthorizationService.HasPermissionAsync(userId, contextKey, requirement.PermissionKey))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}
