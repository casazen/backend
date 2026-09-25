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
    /// Blocks or unblocks the Auth0 account (<c>PATCH /users/{id}</c> with <c>blocked</c>, scope <c>update:users</c>).
    /// A blocked user cannot log in and gets no new access token, not even through a refresh token (PL-03).
    /// </summary>
    Task<Auth0SyncResult> SetBlockedAsync(string userId, bool blocked, CancellationToken cancellationToken = default);

    /// <summary>
    /// The CasaZen roles the user holds in Auth0 (<c>GET /users/{id}/roles</c>); roles of other applications of the
    /// tenant are left out. Used before a deactivation removes them, so the reactivation gives back exactly those.
    /// </summary>
    Task<Auth0UserRolesResult> GetUserRolesAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches email, name and <c>email_verified</c> from Auth0 (not cached). Returns null when Management API is not
    /// configured or the call fails.
    /// </summary>
    Task<Auth0UserProfile?> GetUserProfileAsync(string userId);
}

/// <summary>Outcome of <see cref="IAuth0ManagementService.GetUserRolesAsync"/>: <see cref="Roles"/> is empty unless it succeeded.</summary>
public sealed record Auth0UserRolesResult(Auth0SyncResult Sync, IReadOnlyList<UserRole> Roles)
{
    public static Auth0UserRolesResult Failed(Auth0SyncResult sync) => new(sync, []);
}
