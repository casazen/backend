using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Auth0 Management API operations used to keep Auth0 roles aligned with CasaZen.
/// Role changes are explicit: assignment is additive only and removal targets named roles,
/// so a user with several roles (host + supplier, admin + host) never loses the others.
/// Every mutating call returns an <see cref="Auth0SyncResult"/>: failures are reported to the
/// caller instead of being swallowed.
/// </summary>
public interface IAuth0ManagementService
{
    /// <summary>True when a Management API credential (M2M client or legacy token) is configured.</summary>
    bool IsConfigured { get; }

    /// <summary>Adds <paramref name="role"/> to the user without touching any other role.</summary>
    Task<Auth0SyncResult> AssignRoleAsync(string userId, UserRole role, CancellationToken cancellationToken = default);

    /// <summary>Adds all <paramref name="roles"/> to the user without touching any other role.</summary>
    Task<Auth0SyncResult> AssignRolesAsync(
        string userId,
        IReadOnlyCollection<UserRole> roles,
        CancellationToken cancellationToken = default);

    /// <summary>Removes exactly <paramref name="role"/> from the user (admin role changes).</summary>
    Task<Auth0SyncResult> RemoveRoleAsync(string userId, UserRole role, CancellationToken cancellationToken = default);

    /// <summary>Removes exactly the listed <paramref name="roles"/> from the user.</summary>
    Task<Auth0SyncResult> RemoveRolesAsync(
        string userId,
        IReadOnlyCollection<UserRole> roles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches email and name from Auth0. Returns null when Management API is not configured or the call fails.
    /// </summary>
    Task<Auth0UserProfile?> GetUserProfileAsync(string userId);
}
