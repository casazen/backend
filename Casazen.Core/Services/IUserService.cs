using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public interface IUserService
{
    Task<User?> GetUserAsync(string id);
    Task<User?> GetUserByEmailAsync(string email);
    Task<User> RegisterUserAsync(string email, string firstName, string lastName, string password);
    Task<User> UpdateUserAsync(User user);
    Task<bool> DeleteUserAsync(string id);
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
    Task<Auth0SyncResult> ChangeRoleAsync(string id, UserRole newRole, string adminSub);

    /// <summary>
    /// Completes or updates onboarding: persists rental type, writes the context memberships of every
    /// selected role and syncs the Auth0 onboarding roles. <c>RoleSync</c> reports the Auth0 outcome;
    /// the DB changes are kept even when Auth0 fails.
    /// </summary>
    Task<(User User, IReadOnlyList<string> RolesAssigned, Auth0SyncResult RoleSync)> CompleteOnboardingAsync(
        string sub,
        RentalType rentalType,
        PlanTier planTier,
        string email,
        string firstName,
        string lastName);
}
