using System.Security.Claims;
using Casazen.Core.Authorization;
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

/// <summary>
/// Evaluates the context policies. When a host context is refused because the host onboarding is not complete
/// (PL-02), the failure carries <see cref="HostOnboarding.RequiredCode"/>: the API then answers 403
/// <c>onboarding_required</c> (<see cref="OnboardingRequiredAuthorizationResultHandler"/>) instead of the generic
/// <c>forbidden</c>, so the clients can send the user to the onboarding.
/// </summary>
public class ContextAuthorizationHandler(
    IContextAuthorizationService contextAuthorizationService,
    IHostOnboardingGate hostOnboardingGate) : AuthorizationHandler<ContextPermissionRequirement>
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

        if (requirement.ContextKeys.Any(HostOnboarding.IsHostContext) &&
            !(await hostOnboardingGate.GetStatusAsync(userId)).IsComplete)
        {
            context.Fail(new AuthorizationFailureReason(this, HostOnboarding.RequiredCode));
        }
    }
}
