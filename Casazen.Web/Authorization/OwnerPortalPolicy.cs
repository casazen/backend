using System.Security.Claims;
using Casazen.Web.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

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
    private readonly string? _customClaimNamespace;

    public OwnerPortalAuthorizationHandler(IOptions<Auth0Options> auth0Options)
    {
        _customClaimNamespace = auth0Options.Value.CustomClaimNamespace;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        OwnerPortalRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return Task.CompletedTask;

        // Check for context=owner_portal claim (ADR-011-R5)
        var contextClaim = context.User.FindFirstValue(OwnerPortalClaims.Type(_customClaimNamespace, OwnerPortalClaims.Context, "context"));
        if (contextClaim != "owner_portal")
            return Task.CompletedTask;

        // Verify owner_id claim exists (ADR-011-R7)
        var ownerIdClaim = context.User.FindFirstValue(OwnerPortalClaims.Type(_customClaimNamespace, OwnerPortalClaims.OwnerId, "owner_id"));
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
/// The owner portal custom claims that the Auth0 Action adds to the tokens (ADR-011-R7, runbook auth0.md). The full
/// names are the claim namespaces of the Action, not addresses of a site: they are the ones listed in
/// <c>NoHardcodedPublicDomainTests</c>, like the role, email and name claims.
/// </summary>
public static class OwnerPortalClaims
{
    public const string Context = "https://casazen.app/context";
    public const string OwnerId = "https://casazen.app/owner_id";
    public const string AgencyIds = "https://casazen.app/agency_ids";
    public const string OwnedPropertyIds = "https://casazen.app/owned_property_ids";

    /// <summary>
    /// The claim type to read: the documented full name, or <c>{override}/{name}</c> when a namespace override is
    /// configured (tests, <see cref="Casazen.Web.Configuration.Auth0Options.CustomClaimNamespace"/>).
    /// </summary>
    public static string Type(string? customClaimNamespace, string documentedClaim, string name) =>
        string.IsNullOrWhiteSpace(customClaimNamespace) ? documentedClaim : $"{customClaimNamespace}/{name}";
}

/// <summary>
/// Extensions to read owner portal claims from ClaimsPrincipal (ADR-011-R7).
/// Read the claims under their documented names (<see cref="OwnerPortalClaims"/>) unless a namespace is passed.
/// </summary>
public static class OwnerPortalPrincipalExtensions
{
    /// <summary>
    /// Gets the OwnerUserId from the JWT (ADR-011-R7). Returns null if not an owner portal context.
    /// </summary>
    /// <param name="user">The claims principal to read from.</param>
    /// <param name="customClaimNamespace">An Auth0 custom claim namespace to use instead of the documented claim names.</param>
    public static Guid? GetOwnerUserId(this ClaimsPrincipal user, string? customClaimNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var ownerIdClaim = user.FindFirstValue(OwnerPortalClaims.Type(customClaimNamespace, OwnerPortalClaims.OwnerId, "owner_id"));
        return Guid.TryParse(ownerIdClaim, out var ownerId) ? ownerId : null;
    }

    /// <summary>
    /// Gets the agency IDs from the JWT (ADR-011-R7). Returns empty list if not an owner portal context.
    /// </summary>
    /// <param name="user">The claims principal to read from.</param>
    /// <param name="customClaimNamespace">An Auth0 custom claim namespace to use instead of the documented claim names.</param>
    public static IReadOnlyList<Guid> GetOwnerAgencyIds(this ClaimsPrincipal user, string? customClaimNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var agencyIdsClaim = user.FindFirstValue(OwnerPortalClaims.Type(customClaimNamespace, OwnerPortalClaims.AgencyIds, "agency_ids"));
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
    /// <param name="user">The claims principal to read from.</param>
    /// <param name="customClaimNamespace">An Auth0 custom claim namespace to use instead of the documented claim names.</param>
    public static IReadOnlyList<Guid> GetOwnedPropertyIds(this ClaimsPrincipal user, string? customClaimNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(user);
        var propertyIdsClaim = user.FindFirstValue(OwnerPortalClaims.Type(customClaimNamespace, OwnerPortalClaims.OwnedPropertyIds, "owned_property_ids"));
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
