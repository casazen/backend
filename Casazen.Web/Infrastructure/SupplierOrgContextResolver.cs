using System.Security.Claims;
using Casazen.Core.Services;

namespace Casazen.Web.Infrastructure;

/// <summary>
/// Resolves the supplier org for <c>/api/supplier/*</c> routes.
/// Unlike <see cref="IOrgContextResolver"/>, never provisions a host org.
/// </summary>
public interface ISupplierOrgContextResolver
{
    Task<Guid?> GetOrProvisionSupplierOrgIdAsync(CancellationToken cancellationToken = default);
}

public sealed class SupplierOrgContextResolver(
    IHttpContextAccessor httpContextAccessor,
    IUserService userService,
    ISupplierService supplierService) : ISupplierOrgContextResolver
{
    public async Task<Guid?> GetOrProvisionSupplierOrgIdAsync(CancellationToken cancellationToken = default)
    {
        var sub = ResolveSub();
        if (string.IsNullOrWhiteSpace(sub))
            return null;

        var (jwtEmail, firstName, lastName) = ResolveProfileClaims();
        var user = await userService.GetCurrentUserAsync(sub, jwtEmail, firstName, lastName);

        // Use the DB-backed email when the JWT email claim is missing.
        // Without a valid email, GetOrProvisionSupplierOrgIdAsync cannot match
        // existing profiles and will auto-provision a duplicate on every request.
        var email = !string.IsNullOrWhiteSpace(jwtEmail)
            ? jwtEmail
            : (!string.IsNullOrWhiteSpace(user?.Email) ? user.Email : jwtEmail);

        return await supplierService.GetOrProvisionSupplierOrgIdAsync(
            sub, email, firstName, lastName, cancellationToken);
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
