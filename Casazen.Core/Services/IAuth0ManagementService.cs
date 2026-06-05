using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public interface IAuth0ManagementService
{
    Task AssignRoleAsync(string userId, UserRole role);

    /// <summary>
    /// Replaces onboarding-related roles (PropertyOwner, LongTermLandlord) while preserving others (e.g. Admin).
    /// </summary>
    Task AssignOnboardingRolesAsync(string userId, IReadOnlyList<UserRole> roles);
}
