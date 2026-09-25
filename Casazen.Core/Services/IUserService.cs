using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IUserService
{
    Task<User?> GetUserAsync(string id);
    Task<User?> GetUserByEmailAsync(string email);
    Task<User> RegisterUserAsync(string email, string firstName, string lastName, string password);
    Task<User> UpdateUserAsync(User user);

    /// <summary>
    /// Admin deactivation (PL-03, A1-04). The DB flag is set first and always stands: from the next request the API
    /// refuses the user with 403 <c>account_inactive</c>. Then Auth0 is updated: the account is blocked (no new token)
    /// and its CasaZen roles are removed and remembered in <see cref="User.SuspendedAuth0Roles"/>. The Auth0 outcome is
    /// returned, never thrown; repeating the call on an inactive user retries only the Auth0 part.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// <see cref="UserActivationErrors.CannotDeactivateSelf"/> or <see cref="UserActivationErrors.LastActiveAdmin"/>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user does not exist.</exception>
    Task<UserActivationResult> DeactivateUserAsync(string id, string actorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inverse of <see cref="DeactivateUserAsync"/>: sets the DB flag back, gives back the Auth0 roles removed by the
    /// deactivation and unblocks the Auth0 account (only once the roles are back). Repeating it retries the Auth0 part.
    /// </summary>
    /// <exception cref="Casazen.Core.Exceptions.NotFoundException">The user does not exist.</exception>
    Task<UserActivationResult> ReactivateUserAsync(string id, string actorId, CancellationToken cancellationToken = default);
    Task<bool> ValidateCredentialsAsync(string email, string password);

    /// <summary>
    /// Upsert: returns existing User by sub, or creates a new one from JWT claims.
    /// </summary>
    Task<User> GetCurrentUserAsync(string sub, string email, string firstName, string lastName);

    Task<(IEnumerable<User> Users, int TotalCount)> GetPagedAsync(
        string? search, string? role, bool? isActive, int page, int pageSize);

    /// <summary>
    /// Backfills missing email/name from Auth0 Management API for admin user listings.
    /// </summary>
    Task EnrichUsersFromAuth0Async(IList<User> users);

    /// <summary>
    /// Admin role change: adds <paramref name="newRole"/> and removes only the previous primary role,
    /// in Auth0 first and then in the DB (role + context memberships). When Auth0 fails nothing is
    /// changed in the DB and the failed result is returned, so the admin can retry.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The user does not exist.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// <see cref="UserActivationErrors.UserInactive"/>: the roles of a deactivated user are suspended, reactivate it first.
    /// </exception>
    Task<Auth0SyncResult> ChangeRoleAsync(string id, UserRole newRole, string adminSub);

    /// <summary>
    /// The roles <paramref name="id"/> currently holds in Auth0, restricted to <see cref="AdminManageableRoles.All"/>
    /// (A1-17). Read fresh (not cached) so the admin's multi-role dialog reflects reality even after an out-of-band
    /// change; <see cref="Auth0UserRolesResult.Sync"/> tells whether the read succeeded.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The user does not exist.</exception>
    Task<Auth0UserRolesResult> GetRolesAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin multi-role change (A1-17): <paramref name="roles"/> becomes the user's exact role set among
    /// <see cref="AdminManageableRoles.All"/> (<see cref="UserRole.Guest"/>, <see cref="UserRole.Staff"/> and
    /// <see cref="UserRole.PropertyManager"/> have no app context and are not managed here). Compares it against the
    /// roles currently held in Auth0 and grants/revokes only the difference, in Auth0 first and then in the DB
    /// (primary <see cref="User.Role"/> — the highest-priority role of <see cref="AdminManageableRoles.All"/> still
    /// held, or <see cref="UserRole.None"/> — and context memberships). When Auth0 fails (including when the current
    /// roles cannot be read) nothing changes and the failed result is returned, so the admin can retry.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The user does not exist.</exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// <see cref="UserActivationErrors.UserInactive"/>: the roles of a deactivated user are suspended, reactivate it
    /// first. <see cref="UserActivationErrors.LastActiveAdmin"/>: removing Admin would leave no active administrator.
    /// </exception>
    Task<RoleSetUpdateResult> UpdateRolesAsync(
        string id, IReadOnlyCollection<UserRole> roles, string adminSub, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes or updates onboarding: persists rental type, writes the context memberships of every
    /// selected role and syncs the Auth0 onboarding roles. <c>RoleSync</c> reports the Auth0 outcome;
    /// the DB changes are kept even when Auth0 fails.
    /// </summary>
    Task<(User User, IReadOnlyList<string> RolesAssigned, Auth0SyncResult RoleSync)> CompleteOnboardingAsync(
        string sub,
        RentalType rentalType,
        string email,
        string firstName,
        string lastName);
}

/// <summary>
/// Outcome of a deactivation or reactivation. <see cref="Changed"/> is false when the user already had that state.
/// <see cref="Auth0Sync"/> tells whether Auth0 (block flag and roles) follows the DB; <see cref="RestoredRoles"/> lists the
/// Auth0 roles given back by a reactivation.
/// </summary>
public sealed record UserActivationResult(
    User User,
    bool Changed,
    Auth0SyncResult Auth0Sync,
    IReadOnlyList<UserRole> RestoredRoles);

/// <summary>Stable codes (HTTP 422) of the refused deactivations and role changes (PL-03).</summary>
public static class UserActivationErrors
{
    /// <summary>An admin cannot deactivate the account they are using.</summary>
    public const string CannotDeactivateSelf = "cannot_deactivate_self";

    /// <summary>The user is the last active platform admin (<see cref="UserRole.Admin"/> in the DB).</summary>
    public const string LastActiveAdmin = "last_active_admin";

    /// <summary>The user is deactivated: its roles are suspended until the reactivation.</summary>
    public const string UserInactive = "user_inactive";
}

/// <summary>
/// Roles an admin can grant or revoke individually with <see cref="IUserService.UpdateRolesAsync"/> (A1-17): only
/// the roles mapped to a real app context (<c>ContextAccessBootstrap</c>). <see cref="UserRole.Guest"/>,
/// <see cref="UserRole.Staff"/> and <see cref="UserRole.PropertyManager"/> have none and are left out — assigning
/// them made no sense in the old single-role dialog either.
/// </summary>
public static class AdminManageableRoles
{
    /// <summary>
    /// In priority order: after an update, the primary <see cref="User.Role"/> is the first of these still present
    /// in the new role set, or <see cref="UserRole.None"/> when none is (mirrors the onboarding's own
    /// <c>roles[0]</c> convention, generalized to admin-managed roles).
    /// </summary>
    public static readonly IReadOnlyList<UserRole> All =
    [
        UserRole.Admin,
        UserRole.PropertyOwner,
        UserRole.LongTermLandlord,
        UserRole.Supplier,
    ];
}

/// <summary>
/// Outcome of <see cref="IUserService.UpdateRolesAsync"/>. <see cref="Roles"/> is the resulting role set only when
/// <see cref="RoleSync"/> succeeded; on failure it is empty and the caller keeps whatever it last knew, since
/// nothing changed.
/// </summary>
public sealed record RoleSetUpdateResult(
    User User,
    IReadOnlyList<UserRole> Roles,
    IReadOnlyList<UserRole> RolesGranted,
    IReadOnlyList<UserRole> RolesRevoked,
    Auth0SyncResult RoleSync);
