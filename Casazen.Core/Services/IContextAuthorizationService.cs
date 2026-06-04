namespace Casazen.Core.Services;

public interface IContextAuthorizationService
{
    Task<IReadOnlyList<ContextAccess>> GetUserContextsAsync(string userId, CancellationToken cancellationToken = default);
    Task<bool> HasPermissionAsync(string userId, string contextKey, string permissionKey, CancellationToken cancellationToken = default);
}

public record ContextAccess(
    string ContextKey,
    string DisplayName,
    string RoleKey,
    IReadOnlyList<string> Permissions,
    string DefaultRoute
);
