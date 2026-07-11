using System.Security.Claims;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Resolves the supplier org for <c>/api/supplier/*</c> routes.
/// Unlike <see cref="IOrgContextResolver"/>, never provisions a host org.
/// </summary>
public interface ISupplierOrgContextResolver
{
    Task<Guid?> GetOrProvisionSupplierOrgIdAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves only an already-linked supplier org. This is for business
    /// actions where silently creating or email-linking supplier membership
    /// would be an authorization side effect.
    /// </summary>
    Task<Guid?> GetLinkedSupplierOrgIdAsync(CancellationToken cancellationToken = default);
}

public sealed class SupplierOrgContextResolver(
    IHttpContextAccessor httpContextAccessor,
    IUserService userService,
    ISupplierService supplierService,
    AppDbContext db,
    IAuth0ManagementService auth0Management) : ISupplierOrgContextResolver
{
    public async Task<Guid?> GetOrProvisionSupplierOrgIdAsync(CancellationToken cancellationToken = default)
    {
        var sub = ResolveSub();
        if (string.IsNullOrWhiteSpace(sub))
            return null;

        var (jwtEmail, firstName, lastName) = ResolveProfileClaims();
        var user = await userService.GetCurrentUserAsync(sub, jwtEmail, firstName, lastName);

        // Resolve email: JWT claim → DB record → Auth0 Management API
        var email = ResolveEmail(jwtEmail, user);
        if (string.IsNullOrWhiteSpace(email) && user is not null)
        {
            // Last resort: fetch from Auth0 Management API. This call is cached
            // by the underlying service and only happens once per user whose JWT
            // lacks the email claim.
            var profile = await auth0Management.GetUserProfileAsync(sub);
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.Email))
            {
                email = profile.Email;
                // Backfill the DB so future requests don't need the API call
                user.Email = email;
                await userService.UpdateUserAsync(user);
            }
        }

        var orgId = await supplierService.GetOrProvisionSupplierOrgIdAsync(
            sub, email, firstName, lastName, cancellationToken);

        // Fire-and-forget: ensure the user has the Supplier role in Auth0.
        // The user may have signed up via Auth0 before the Supplier role was
        // assigned during registration (or registration was done anonymously).
        // Silently skips if the Management API token is not configured.
        if (orgId is not null)
        {
            _ = auth0Management.AssignRoleAsync(sub, UserRole.Supplier);
        }

        return orgId;
    }

    public async Task<Guid?> GetLinkedSupplierOrgIdAsync(CancellationToken cancellationToken = default)
    {
        var sub = ResolveSub();
        if (string.IsNullOrWhiteSpace(sub))
            return null;

        var user = await userService.GetUserAsync(sub);
        if (user is null)
            return null;

        if (user.SupplierOrgId is Guid supplierOrgId &&
            await HasSupplierOrgWithProfileAsync(supplierOrgId, cancellationToken))
        {
            return supplierOrgId;
        }

        if (user.OrgId is Guid linkedOrgId &&
            await HasSupplierOrgWithProfileAsync(linkedOrgId, cancellationToken))
        {
            return linkedOrgId;
        }

        return null;
    }

    private async Task<bool> HasSupplierOrgWithProfileAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var isSupplierOrg = await db.Orgs.AsNoTracking()
            .AnyAsync(o => o.Id == orgId && o.OrgType == OrgType.Supplier, cancellationToken);
        if (!isSupplierOrg)
            return false;

        return await db.SupplierProfiles.AsNoTracking()
            .AnyAsync(sp => sp.OrgId == orgId, cancellationToken);
    }

    private static string ResolveEmail(string jwtEmail, Core.Entities.User? user)
    {
        if (!string.IsNullOrWhiteSpace(jwtEmail))
            return jwtEmail;
        if (user is not null && !string.IsNullOrWhiteSpace(user.Email))
            return user.Email;
        return string.Empty;
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
