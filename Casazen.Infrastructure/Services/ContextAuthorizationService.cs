using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Resolves the contexts (short-rent, long-rent, admin, supplier) and permissions of a user by merging
/// DB memberships with JWT roles. DB data comes from <see cref="IUserAuthorizationSnapshotStore"/>, so
/// a request evaluating several context policies reads the DB at most once (and not at all while the
/// short-lived cache is warm).
/// </summary>
public class ContextAuthorizationService(
    IUserAuthorizationSnapshotStore snapshotStore,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ContextAuthorizationService> logger) : IContextAuthorizationService
{
    public async Task<IReadOnlyList<ContextAccess>> GetUserContextsAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await snapshotStore.GetAsync(userId, cancellationToken);
            return BuildContexts(snapshot);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Unexpected error in GetUserContextsAsync for user {UserId}", userId);
            // Fallback to JWT-only contexts on any DB error
            return ContextAccessBootstrap.BuildFallbackAccess(ResolveJwtRoles());
        }
    }

    private IReadOnlyList<ContextAccess> BuildContexts(UserAuthorizationSnapshot snapshot)
    {
        var memberships = snapshot.Memberships;
        var jwtRoles = ResolveJwtRoles();

        if (jwtRoles.Count == 0 && memberships.Count == 0 && snapshot.Exists)
        {
            jwtRoles = MapDbUserRoleToJwtRoles(snapshot.Role);
        }

        if (memberships.Count > 0)
        {
            var fromDb = memberships.ToList();

            var existingKeys = fromDb.Select(c => c.ContextKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var jwtContext in ContextAccessBootstrap.BuildFallbackAccess(jwtRoles))
            {
                if (!existingKeys.Contains(jwtContext.ContextKey))
                {
                    fromDb.Add(jwtContext);
                }
            }

            return fromDb.OrderBy(c => c.ContextKey, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return ContextAccessBootstrap.BuildFallbackAccess(jwtRoles);
    }

    public async Task<bool> HasPermissionAsync(
        string userId,
        string contextKey,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await snapshotStore.GetAsync(userId, cancellationToken);
            if (snapshot is { Exists: true, IsActive: false })
            {
                return false;
            }

            var contexts = BuildContexts(snapshot);
            var context = contexts.FirstOrDefault(c => string.Equals(c.ContextKey, contextKey, StringComparison.OrdinalIgnoreCase));
            if (context is null)
            {
                if (IsSharedPropertyPermission(contextKey, permissionKey) &&
                    contexts.Any(c =>
                        IsRentalContext(c.ContextKey) &&
                        c.Permissions.Contains(permissionKey, StringComparer.OrdinalIgnoreCase)))
                {
                    return true;
                }

                logger.LogDebug(
                    "Permission denied: user {UserId} has no context {ContextKey}",
                    userId, contextKey);
                return false;
            }

            if (string.IsNullOrWhiteSpace(permissionKey))
            {
                return true;
            }

            var hasPermission = context.Permissions.Contains(permissionKey, StringComparer.OrdinalIgnoreCase);
            if (!hasPermission && IsSharedPropertyPermission(contextKey, permissionKey))
            {
                hasPermission = contexts.Any(c =>
                    !string.Equals(c.ContextKey, contextKey, StringComparison.OrdinalIgnoreCase) &&
                    IsRentalContext(c.ContextKey) &&
                    c.Permissions.Contains(permissionKey, StringComparer.OrdinalIgnoreCase));
            }

            if (!hasPermission)
            {
                logger.LogDebug(
                    "Permission denied: user {UserId} lacks {PermissionKey} in {ContextKey}",
                    userId, permissionKey, contextKey);
            }

            return hasPermission;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unexpected error in HasPermissionAsync for user {UserId}, context {ContextKey}, permission {PermissionKey}",
                userId, contextKey, permissionKey);
            return false;
        }
    }

    private static bool IsSharedPropertyPermission(string contextKey, string permissionKey) =>
        IsRentalContext(contextKey) &&
        (string.Equals(permissionKey, "property.read", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(permissionKey, "property.write", StringComparison.OrdinalIgnoreCase));

    private static bool IsRentalContext(string contextKey) =>
        string.Equals(contextKey, "short-rent", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contextKey, "long-rent", StringComparison.OrdinalIgnoreCase);

    private IReadOnlyList<string> ResolveJwtRoles()
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null)
        {
            return [];
        }

        var claimValues = principal.FindAll("https://casazen.app/roles").Select(c => c.Value)
            .Concat(principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Concat(principal.FindAll("roles").Select(c => c.Value));

        return ParseRoles(claimValues);
    }

    private static IReadOnlyList<string> MapDbUserRoleToJwtRoles(UserRole role) =>
        role switch
        {
            UserRole.Admin => ["Admin"],
            UserRole.PropertyOwner => ["PropertyOwner"],
            UserRole.PropertyManager => ["PropertyOwner"],
            UserRole.LongTermLandlord => ["LongTermLandlord"],
            _ => [],
        };

    internal static string GetDefaultRoute(string contextKey) =>
        contextKey switch
        {
            "short-rent" => "/app/short-rent",
            "long-rent" => "/app/long-rent/leases",
            "admin" => "/app/admin",
            "supplier" => "/supplier/inbox",
            _ => "/app/choose-context",
        };

    private static IReadOnlyList<string> ParseRoles(IEnumerable<string?> claimValues)
    {
        var roles = new List<string>();
        foreach (var value in claimValues)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith('['))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<string[]>(trimmed);
                    if (parsed is { Length: > 0 })
                    {
                        roles.AddRange(parsed.Where(r => !string.IsNullOrWhiteSpace(r))!);
                        continue;
                    }
                }
                catch (JsonException)
                {
                }
            }

            roles.Add(trimmed);
        }

        return roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
