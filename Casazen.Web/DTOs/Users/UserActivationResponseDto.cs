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

    public bool Changed { get; set; }

    public bool Auth0Synced { get; set; }

    public string? Auth0SyncError { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>Reactivation: the Auth0 roles given back (removed by the deactivation). Empty otherwise.</summary>
    public IReadOnlyList<string> RolesRestored { get; set; } = [];
}
