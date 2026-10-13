namespace Casazen.Web.DTOs.Users;

/// <summary>
/// Outcome of <c>DELETE /api/users/{id}</c> (deactivation) and <c>POST /api/users/{id}/reactivate</c> (PL-03). The DB
/// change is always applied; <see cref="Auth0Synced"/> says whether Auth0 followed it (account blocked and roles removed,
/// or roles given back and account unblocked). When it did not, repeating the same call retries only the Auth0 part.
/// </summary>
public sealed class UserActivationResponseDto
{
    public string Id { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    /// <summary>False when the user already had this state (the call only retried the Auth0 update).</summary>
    public bool Changed { get; set; }

    public bool Auth0Synced { get; set; }

    /// <summary>
    /// Stable code of the Auth0 failure (<c>auth0_management_not_configured</c>, <c>auth0_management_token_failed</c>,
    /// <c>auth0_rate_limited</c>, <c>auth0_management_error</c>...), null when <see cref="Auth0Synced"/>.
    /// </summary>
    public string? Auth0SyncError { get; set; }

    /// <summary>Localized outcome for the admin, explaining what to do when Auth0 was not updated.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Reactivation: the Auth0 roles given back (removed by the deactivation). Empty otherwise.</summary>
    public IReadOnlyList<string> RolesRestored { get; set; } = [];
}
