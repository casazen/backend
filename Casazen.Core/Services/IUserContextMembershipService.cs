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

    /// <summary>
    /// True when the user holds a DB membership of a host context with a role other than the owner's
    /// (<see cref="Casazen.Core.Authorization.OrgOwnerRoles.IsHostMemberRole"/>): it is a <b>member</b> of an org
    /// (collaborator, property manager, accountant…), not its owner. Read from the database on every call, never from
    /// the authorization cache: it guards a self-service action (the onboarding, AM-00).
    /// </summary>
    Task<bool> IsHostMemberAsync(string userId, CancellationToken cancellationToken = default);
}
