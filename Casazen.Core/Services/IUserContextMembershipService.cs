using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Keeps <see cref="UserContextMembership"/> rows aligned with the roles a user holds, so backend
/// context authorization does not depend on the roles carried by the JWT.
/// </summary>
public interface IUserContextMembershipService
{
    /// <summary>
    /// Creates (or re-points) the membership of every context mapped by <paramref name="roles"/>.
    /// Roles without a DB-backed context (e.g. Supplier, which derives from <c>User.SupplierOrgId</c>) are ignored.
    /// </summary>
    Task GrantAsync(string userId, IEnumerable<UserRole> roles, CancellationToken cancellationToken = default);

    /// <summary>Deletes the membership of every context mapped by <paramref name="roles"/>.</summary>
    Task RevokeAsync(string userId, IEnumerable<UserRole> roles, CancellationToken cancellationToken = default);
}
