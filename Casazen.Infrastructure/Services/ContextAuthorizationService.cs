using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

public class ContextAuthorizationService(
    AppDbContext dbContext,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ContextAuthorizationService> logger) : IContextAuthorizationService
{
    public async Task<IReadOnlyList<ContextAccess>> GetUserContextsAsync(string userId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetUserContextsInternalAsync(userId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in GetUserContextsAsync for user {UserId}", userId);
            // Fallback to JWT-only contexts on any DB error
            var jwtRoles = ResolveJwtRoles();
            if (jwtRoles.Count == 0)
            {
                jwtRoles = await FallbackToDbUserRolesAsync(userId, cancellationToken);
            }
            return ContextAccessBootstrap.BuildFallbackAccess(jwtRoles);
        }
    }

    private async Task<IReadOnlyList<ContextAccess>> GetUserContextsInternalAsync(string userId, CancellationToken cancellationToken)
    {
        var memberships = await dbContext.UserContextMemberships
            .AsNoTracking()
            .Include(m => m.Context)
            .Include(m => m.Role)
            .ThenInclude(r => r.Permissions)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.ContextKey)
            .ToListAsync(cancellationToken);

        var jwtRoles = ResolveJwtRoles();

        if (jwtRoles.Count == 0 && memberships.Count == 0)
        {
            var dbUser = await dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            if (dbUser is not null)
            {
                jwtRoles = MapDbUserRoleToJwtRoles(dbUser.Role);
            }
        }

        if (memberships.Count > 0)
        {
            var fromDb = memberships.Select(m => new ContextAccess(
                    m.ContextKey,
                    m.Context.DisplayName,
                    m.Role.RoleKey,
                    m.Role.Permissions.Select(p => p.PermissionKey).OrderBy(p => p).ToList(),
                    GetDefaultRoute(m.ContextKey)))
                .ToList();

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
            var user = await dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            if (user is { IsActive: false })
            {
                return false;
            }

            var contexts = await GetUserContextsAsync(userId, cancellationToken);
            var context = contexts.FirstOrDefault(c => string.Equals(c.ContextKey, contextKey, StringComparison.OrdinalIgnoreCase));
            if (context is null)
            {
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

    private async Task<IReadOnlyList<string>> FallbackToDbUserRolesAsync(string userId, CancellationToken cancellationToken)
    {
        try
        {
            var dbUser = await dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

            if (dbUser is not null)
            {
                return MapDbUserRoleToJwtRoles(dbUser.Role);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fallback to DB user roles for {UserId}", userId);
        }

        return [];
    }

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

    private static string GetDefaultRoute(string contextKey) =>
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
