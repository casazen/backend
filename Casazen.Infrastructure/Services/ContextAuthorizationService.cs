using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Casazen.Infrastructure.Services;

public class ContextAuthorizationService(
    AppDbContext dbContext,
    IHttpContextAccessor httpContextAccessor) : IContextAuthorizationService
{
    public async Task<IReadOnlyList<ContextAccess>> GetUserContextsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var memberships = await dbContext.UserContextMemberships
            .AsNoTracking()
            .Include(m => m.Context)
            .Include(m => m.Role)
            .ThenInclude(r => r.Permissions)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.ContextKey)
            .ToListAsync(cancellationToken);

        var jwtRoles = ParseRoles(httpContextAccessor.HttpContext?.User.FindAll("https://casazen.app/roles").Select(c => c.Value) ?? []);

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
            return false;
        }

        if (string.IsNullOrWhiteSpace(permissionKey))
        {
            return true;
        }

        return context.Permissions.Contains(permissionKey, StringComparer.OrdinalIgnoreCase);
    }

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
