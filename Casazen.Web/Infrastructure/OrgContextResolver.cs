using System.Security.Claims;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Resolves the caller's organization id.
/// </summary>
/// <remarks>
/// It no longer creates an org (PL-02, A1-05): the first org of a user is created only by the onboarding, together
/// with the legal consents. A caller without an org gets <c>null</c>; the host endpoints never get that far, since
/// their policies already answer 403 <c>onboarding_required</c> to a user without a completed onboarding. The name is
/// kept for the existing callers.
/// </remarks>
public interface IOrgContextResolver
{
    Task<Guid?> GetOrProvisionOrgIdAsync(CancellationToken cancellationToken = default);
}

public sealed class OrgContextResolver(
    IRequestTenantContext tenantContext,
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

        var user = await userService.GetCurrentUserAsync(sub, email, firstName, lastName);
        if (user.OrgId is Guid linked)
        {
            var linkedOrg = await orgService.GetByIdAsync(linked, cancellationToken);
            if (linkedOrg?.OrgType == OrgType.Host)
            {
                // Linked after the tenant was resolved (e.g. by a parallel first request): scope this request to it.
                tenantContext.SetOrgId(linked);
                return linked;
            }

            // A1-40: User.OrgId points at a non-Host org — typically the caller's own Supplier org, linked at
            // supplier registration before the host onboarding ever ran. Never treat it as the host tenant:
            // fall through as if there were no org yet, so the onboarding provisions a real Host org instead
            // of reusing the supplier one (OrgService.EnsureOrgForUserAsync applies the same rule).
            logger.LogWarning(
                "User {UserId} OrgId {OrgId} is not a Host org (type {OrgType}): treating as no host org yet",
                sub, linked, linkedOrg?.OrgType);
        }

        // PL-02 (A1-05): no org without the onboarding and its consents (POST/PUT /api/users/onboarding).
        logger.LogInformation("User {UserId} has no org yet: the onboarding creates it", sub);
        return null;
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
