namespace Casazen.Core.Services;

public interface IContextAuthorizationService
{
    Task<IReadOnlyList<ContextAccess>> GetUserContextsAsync(string userId, CancellationToken cancellationToken = default);
    Task<bool> HasPermissionAsync(string userId, string contextKey, string permissionKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// As <see cref="HasPermissionAsync"/>, but an error while the memberships are read (a database that does not answer) is thrown, never
    /// answered as «no». For the code that runs in the event bus or in a job and decides who is told something: a person dropped because of
    /// a passing error would be missed for good, while an exception makes the bus (or Hangfire) deliver the event again.
    /// </summary>
    Task<bool> HasPermissionOrThrowAsync(string userId, string contextKey, string permissionKey, CancellationToken cancellationToken = default);
}

public record ContextAccess(
    string ContextKey,
    string DisplayName,
    string RoleKey,
    IReadOnlyList<string> Permissions,
    string DefaultRoute
);
