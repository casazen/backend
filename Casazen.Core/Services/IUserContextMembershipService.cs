using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Keeps <see cref="UserContextMembership"/> rows aligned with the roles a user holds, so backend
/// context authorization does not depend on the roles carried by the JWT. It writes the memberships of the
/// <b>user roles</b> (<c>PropertyOwner</c>, <c>LongTermLandlord</c>, <c>Admin</c>); the memberships a role <b>of an org</b>
/// gives (account, collaborator, property manager, accountant) are written, with the <see cref="OrgMember"/> row, by
/// <see cref="IOrgMembershipService"/> only.
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
    /// True when the user is a <b>member</b> of an org (collaborator, property manager, accountant, administrator…), not
    /// its owner: it holds a DB membership of a host context with a role other than the owner's
    /// (<see cref="Casazen.Core.Authorization.OrgOwnerRoles.IsHostMemberRole"/>), or it has an <see cref="OrgMember"/> row
    /// whose role is not <see cref="Casazen.Core.Entities.Enums.OrgRole.Owner"/> (AM-01: the org membership is the source
    /// of truth, whatever the projection says), or <c>User.OrgId</c> still points at a host org that already has a
    /// different owner (a member removed before that link was cleared). Read from the database on every call, never from
    /// the authorization cache: it guards a self-service action (the onboarding, AM-00).
    /// </summary>
    Task<bool> IsHostMemberAsync(string userId, CancellationToken cancellationToken = default);
}
