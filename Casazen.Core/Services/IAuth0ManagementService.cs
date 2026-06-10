using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IAuth0ManagementService
{
    Task AssignRoleAsync(string userId, UserRole role);

    /// <summary>
    /// Replaces onboarding-related roles (PropertyOwner, LongTermLandlord) while preserving others (e.g. Admin).
    /// </summary>
    Task AssignOnboardingRolesAsync(string userId, IReadOnlyList<UserRole> roles);

    /// <summary>
    /// Fetches email and name from Auth0. Returns null when Management API is not configured.
    /// </summary>
    Task<Auth0UserProfile?> GetUserProfileAsync(string userId);
}
