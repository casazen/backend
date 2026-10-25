using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace Casazen.Web.Authorization;

/// <summary>
/// Requirement for owner portal access (ADR-011-R5).
/// </summary>
public sealed class OwnerPortalRequirement : IAuthorizationRequirement;

/// <summary>
/// Owner portal authorization (ADR-011-R5): checks JWT claim context=owner_portal
/// and resolves OwnerUserId from JWT. MUST NOT include host roles.
/// </summary>
public class OwnerPortalAuthorizationHandler : AuthorizationHandler<OwnerPortalRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnerPortalRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        // Check for context=owner_portal claim (ADR-011-R5)
        var contextClaim = context.User.FindFirstValue("https://casazen.app/context");
        if (contextClaim != "owner_portal")
            return Task.CompletedTask;

        // Verify owner_id claim exists (ADR-011-R7)
        var ownerIdClaim = context.User.FindFirstValue("https://casazen.app/owner_id");
        if (string.IsNullOrWhiteSpace(ownerIdClaim) || !Guid.TryParse(ownerIdClaim, out _))
            return Task.CompletedTask;

        // MUST NOT include host roles (PropertyOwner, Staff, Manager) per ADR-011-R5
        var roles = context.User.Claims
            .Where(c => c.Type == ClaimTypes.Role || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/role")
            .Select(c => c.Value)
            .ToList();

        if (roles.Any(r => r is "PropertyOwner" or "Staff" or "Manager" or "LongTermLandlord"))
        {
            context.Fail(new AuthorizationFailureReason(this, "owner_portal_cannot_have_host_roles"));
            return Task.CompletedTask;
        }

        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Extensions to read owner portal claims from ClaimsPrincipal.
/// </summary>
public static class OwnerPortalPrincipalExtensions
{
    /// <summary>
    /// Gets the OwnerUserId from the JWT (ADR-011-R7). Returns null if not an owner portal context.
    /// </summary>
    public static Guid? GetOwnerUserId(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var ownerIdClaim = user.FindFirstValue("https://casazen.app/owner_id");
        return Guid.TryParse(ownerIdClaim, out var ownerId) ? ownerId : null;
    }

    /// <summary>
    /// Gets the agency IDs from the JWT (ADR-011-R7). Returns empty list if not an owner portal context.
    /// </summary>
    public static IReadOnlyList<Guid> GetOwnerAgencyIds(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var agencyIdsClaim = user.FindFirstValue("https://casazen.app/agency_ids");
        if (string.IsNullOrWhiteSpace(agencyIdsClaim))
            return Array.Empty<Guid>();

        // Parse JSON array or comma-separated list
        try
        {
            // Try JSON array first (e.g., ["uuid1","uuid2"])
            if (agencyIdsClaim.StartsWith('['))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(agencyIdsClaim);
                if (ids is null)
                    return Array.Empty<Guid>();

                return ids.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                          .Where(id => id != Guid.Empty)
                          .ToList();
            }

            // Fallback to comma-separated
            return agencyIdsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => Guid.TryParse(id.Trim(), out var guid) ? guid : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();
        }
        catch
        {
            return Array.Empty<Guid>();
        }
    }

    /// <summary>
    /// Gets the owned property IDs from the JWT (ADR-011-R7). Returns empty list if not an owner portal context.
    /// </summary>
    public static IReadOnlyList<Guid> GetOwnedPropertyIds(this ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var propertyIdsClaim = user.FindFirstValue("https://casazen.app/owned_property_ids");
        if (string.IsNullOrWhiteSpace(propertyIdsClaim))
            return Array.Empty<Guid>();

        // Parse JSON array or comma-separated list
        try
        {
            // Try JSON array first (e.g., ["uuid1","uuid2"])
            if (propertyIdsClaim.StartsWith('['))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(propertyIdsClaim);
                if (ids is null)
                    return Array.Empty<Guid>();

                return ids.Select(id => Guid.TryParse(id, out var guid) ? guid : Guid.Empty)
                          .Where(id => id != Guid.Empty)
                          .ToList();
            }

            // Fallback to comma-separated
            return propertyIdsClaim.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(id => Guid.TryParse(id.Trim(), out var guid) ? guid : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();
        }
        catch
        {
            return Array.Empty<Guid>();
        }
    }
}
