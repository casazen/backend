using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IUserService
{
    Task<User?> GetUserAsync(string id);
    Task<User?> GetUserByEmailAsync(string email);
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
