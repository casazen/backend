namespace Casazen.Core.Services;

public enum Auth0SyncStatus
{
    Synced,
    NotConfigured,
    Failed,
}

/// <summary>
/// Outcome of an Auth0 Management API call. <see cref="ErrorCode"/> is a stable, client-facing code
/// (never an Auth0 message) so API responses can expose it without leaking internals.
/// </summary>
public sealed record Auth0SyncResult(Auth0SyncStatus Status, string? ErrorCode = null)
{
    public const string NotConfiguredCode = "auth0_management_not_configured";
    public const string TokenFailedCode = "auth0_management_token_failed";
    public const string RoleNotFoundCode = "auth0_role_not_found";
    public const string RateLimitedCode = "auth0_rate_limited";
    public const string ApiErrorCode = "auth0_management_error";

    public static Auth0SyncResult Synced { get; } = new(Auth0SyncStatus.Synced);

    public static Auth0SyncResult NotConfigured { get; } = new(Auth0SyncStatus.NotConfigured, NotConfiguredCode);

    public bool Succeeded => Status == Auth0SyncStatus.Synced;

    public static Auth0SyncResult Failed(string errorCode) => new(Auth0SyncStatus.Failed, errorCode);

    /// <summary>First failure wins; <see cref="Synced"/> only when both succeeded.</summary>
    public Auth0SyncResult Combine(Auth0SyncResult other) => Succeeded ? other : this;
}
