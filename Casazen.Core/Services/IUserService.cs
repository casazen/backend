using Casazen.Core.Entities;

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

    Task ChangeRoleAsync(string id, UserRole newRole, string adminSub);

    /// <summary>Replaces all roles for a user (Auth0 + DB).</summary>
    Task<IReadOnlyList<string>> ChangeRolesAsync(string id, IReadOnlyList<UserRole> roles, string adminSub);

    /// <summary>
    /// Completes or updates onboarding: persists rental type, syncs Auth0 onboarding roles.
    /// </summary>
    Task<(User User, IReadOnlyList<string> RolesAssigned)> CompleteOnboardingAsync(
        string sub, RentalType rentalType, string email, string firstName, string lastName);
}
