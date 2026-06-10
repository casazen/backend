using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Resolves the caller's organization id, auto-provisioning a Starter org when the user
/// has roles but never completed onboarding (production backfill for #217).
/// </summary>
public interface IOrgContextResolver
{
    Task<Guid?> GetOrProvisionOrgIdAsync(CancellationToken cancellationToken = default);
}

public sealed class OrgContextResolver(
    ITenantContext tenantContext,
    IHttpContextAccessor httpContextAccessor,
    IUserService userService,
    IOrgService orgService,
    ILogger<OrgContextResolver> logger) : IOrgContextResolver
{
    public async Task<Guid?> GetOrProvisionOrgIdAsync(CancellationToken cancellationToken = default)
    {
        if (tenantContext.OrgId is Guid cached)
            return cached;

        var sub = ResolveSub();
        if (string.IsNullOrWhiteSpace(sub))
            return null;

        var (email, firstName, lastName) = ResolveProfileClaims();
        var displayName = $"{firstName} {lastName}".Trim();
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = string.IsNullOrWhiteSpace(email) ? "La mia organizzazione" : email;

        // Upsert user row first — EnsureOrgForUserAsync requires the User to exist (#217).
        var user = await userService.GetCurrentUserAsync(sub, email, firstName, lastName);
        if (user.OrgId is Guid linked)
            return linked;

        logger.LogInformation("Auto-provisioning Starter org for user {UserId}", sub);
        var org = await orgService.EnsureOrgForUserAsync(
            sub, email, displayName, PlanTier.Starter, cancellationToken);
        return org.Id;
    }

    private string? ResolveSub()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null)
            return null;

        return user.FindFirstValue("sub")
            ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier");
    }

    private (string Email, string FirstName, string LastName) ResolveProfileClaims()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null)
            return (string.Empty, string.Empty, string.Empty);

        var email = principal.FindFirst("email")?.Value
                    ?? principal.FindFirst(ClaimTypes.Email)?.Value
                    ?? string.Empty;
        var firstName = principal.FindFirst("given_name")?.Value
                        ?? principal.FindFirst("name")?.Value?.Split(' ').FirstOrDefault()
                        ?? string.Empty;
        var lastName = principal.FindFirst("family_name")?.Value
                       ?? principal.FindFirst("name")?.Value?.Split(' ').Skip(1).FirstOrDefault()
                       ?? string.Empty;

        return (email, firstName, lastName);
    }
}
