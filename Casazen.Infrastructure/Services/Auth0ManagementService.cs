using System.Net;
using Auth0.Core.Exceptions;
using Auth0.ManagementApi;
using Auth0.ManagementApi.Models;
using Auth0.ManagementApi.Paging;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Wraps the Auth0 Management API for role synchronisation.
/// <list type="bullet">
/// <item>Tokens come from <see cref="IAuth0ManagementTokenProvider"/> (client credentials, cached).</item>
/// <item>Role assignment is additive only: other roles of the user are never removed.
/// Removal is explicit and limited to the named roles.</item>
/// <item>Auth0 role ids are cached in <see cref="IMemoryCache"/>.</item>
/// <item>Every call returns an <see cref="Auth0SyncResult"/>; failures are logged with context and
/// reported to the caller instead of being swallowed.</item>
/// </list>
/// </summary>
public class Auth0ManagementService(
    IAuth0ManagementTokenProvider tokenProvider,
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<Auth0ManagementService> logger) : IAuth0ManagementService
{
    /// <summary>How long the Auth0 role name → id map stays cached.</summary>
    public static readonly TimeSpan RoleIdCacheDuration = TimeSpan.FromHours(1);

    // Map C# enum values to Auth0 role names that match the Auth0 Action output.
    private static readonly Dictionary<UserRole, string> RoleNames = new()
    {
        { UserRole.Admin,            "Admin" },
        { UserRole.PropertyOwner,    "PropertyOwner" },
        { UserRole.PropertyManager,  "PropertyManager" },
        { UserRole.Guest,            "Guest" },
        { UserRole.Staff,            "Staff" },
        { UserRole.LongTermLandlord, "LongTermLandlord" },
        { UserRole.Supplier,         "Supplier" },
    };

    public bool IsConfigured => tokenProvider.IsConfigured;

    public Task<Auth0SyncResult> AssignRoleAsync(
        string userId,
        UserRole role,
        CancellationToken cancellationToken = default) =>
        AssignRolesAsync(userId, [role], cancellationToken);

    public Task<Auth0SyncResult> AssignRolesAsync(
        string userId,
        IReadOnlyCollection<UserRole> roles,
        CancellationToken cancellationToken = default) =>
        ChangeRolesAsync(userId, roles, remove: false, cancellationToken);

    public Task<Auth0SyncResult> RemoveRoleAsync(
        string userId,
        UserRole role,
        CancellationToken cancellationToken = default) =>
        RemoveRolesAsync(userId, [role], cancellationToken);

    public Task<Auth0SyncResult> RemoveRolesAsync(
        string userId,
        IReadOnlyCollection<UserRole> roles,
        CancellationToken cancellationToken = default) =>
        ChangeRolesAsync(userId, roles, remove: true, cancellationToken);

    /// <inheritdoc />
    public Task<Auth0SyncResult> SetBlockedAsync(
        string userId,
        bool blocked,
        CancellationToken cancellationToken = default) =>
        CallAsync(
            blocked ? "block user" : "unblock user",
            userId,
            ct => ExecuteAsync(
                client => client.Users.UpdateAsync(userId, new UserUpdateRequest { Blocked = blocked }, ct),
                ct),
            cancellationToken);

    /// <inheritdoc />
    public async Task<Auth0UserRolesResult> GetUserRolesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var roles = new List<UserRole>();
        var sync = await CallAsync(
            "read roles of user",
            userId,
            async ct =>
            {
                const int pageSize = 50;
                for (var page = 0; ; page++)
                {
                    var currentPage = page;
                    var assigned = await ExecuteAsync(
                        client => client.Users.GetRolesAsync(userId, new PaginationInfo(currentPage, pageSize, false), ct),
                        ct);
                    if (assigned is null)
                        break;

                    roles.AddRange(assigned
                        .Select(r => RoleNames.FirstOrDefault(n => string.Equals(n.Value, r.Name, StringComparison.OrdinalIgnoreCase)))
                        .Where(n => n.Value is not null)
                        .Select(n => n.Key));

                    if (assigned.Count < pageSize)
                        break;
                }
            },
            cancellationToken);

        return sync.Succeeded
            ? new Auth0UserRolesResult(sync, roles.Distinct().ToList())
            : Auth0UserRolesResult.Failed(sync);
    }

    /// <inheritdoc />
    public async Task<Auth0UserProfile?> GetUserProfileAsync(string userId)
    {
        if (!tokenProvider.IsConfigured)
            return null;

        try
        {
            var auth0User = await ExecuteAsync(client => client.Users.GetAsync(userId), CancellationToken.None);
            if (auth0User is null)
                return null;

            var email = auth0User.Email ?? string.Empty;
            var firstName = auth0User.FirstName ?? string.Empty;
            var lastName = auth0User.LastName ?? string.Empty;

            if (string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(auth0User.FullName))
            {
                var parts = auth0User.FullName.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                firstName = parts[0];
                if (parts.Length > 1)
                    lastName = parts[1];
            }

            if (string.IsNullOrWhiteSpace(firstName) && !string.IsNullOrWhiteSpace(auth0User.NickName))
                firstName = auth0User.NickName;

            return new Auth0UserProfile(email, firstName, lastName, auth0User.EmailVerified);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Auth0ManagementService: Failed to fetch profile for user {UserId}", userId);
            return null;
        }
    }

    private async Task<Auth0SyncResult> ChangeRolesAsync(
        string userId,
        IReadOnlyCollection<UserRole> roles,
        bool remove,
        CancellationToken cancellationToken)
    {
        var operation = remove ? "remove" : "assign";
        var roleNames = roles
            .Where(RoleNames.ContainsKey)
            .Select(r => RoleNames[r])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roleNames.Count == 0)
            return Auth0SyncResult.Synced;

        if (!tokenProvider.IsConfigured)
        {
            logger.LogWarning(
                "Auth0ManagementService: Management API not configured — cannot {Operation} roles [{Roles}] for user {UserId}",
                operation, string.Join(", ", roleNames), userId);
            return Auth0SyncResult.NotConfigured;
        }

        try
        {
            var roleIds = await ResolveRoleIdsAsync(roleNames, cancellationToken);
            if (roleIds is null)
            {
                logger.LogError(
                    "Auth0ManagementService: Auth0 roles [{Roles}] not found — cannot {Operation} them for user {UserId}",
                    string.Join(", ", roleNames), operation, userId);
                return Auth0SyncResult.Failed(Auth0SyncResult.RoleNotFoundCode);
            }

            var request = new AssignRolesRequest { Roles = roleIds };
            await ExecuteAsync(
                client => remove
                    ? client.Users.RemoveRolesAsync(userId, request, cancellationToken)
                    : client.Users.AssignRolesAsync(userId, request, cancellationToken),
                cancellationToken);

            logger.LogInformation(
                "Auth0ManagementService: {Operation} roles [{Roles}] for user {UserId} succeeded",
                operation, string.Join(", ", roleNames), userId);
            return Auth0SyncResult.Synced;
        }
        catch (Auth0ManagementTokenException ex)
        {
            logger.LogError(ex,
                "Auth0ManagementService: No Management API token — cannot {Operation} roles [{Roles}] for user {UserId}",
                operation, string.Join(", ", roleNames), userId);
            return Auth0SyncResult.Failed(Auth0SyncResult.TokenFailedCode);
        }
        catch (RateLimitApiException ex)
        {
            logger.LogError(ex,
                "Auth0ManagementService: Rate limited while trying to {Operation} roles [{Roles}] for user {UserId}",
                operation, string.Join(", ", roleNames), userId);
            return Auth0SyncResult.Failed(Auth0SyncResult.RateLimitedCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Auth0ManagementService: Failed to {Operation} roles [{Roles}] for user {UserId} (status {StatusCode})",
                operation, string.Join(", ", roleNames), userId, (ex as ErrorApiException)?.StatusCode);
            return Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode);
        }
    }

    /// <summary>
    /// Runs one Management API <paramref name="call"/> for <paramref name="userId"/> and maps its outcome like the role
    /// sync: not configured, token failure, rate limit or API error are logged and returned, never thrown.
    /// </summary>
    private async Task<Auth0SyncResult> CallAsync(
        string operation,
        string userId,
        Func<CancellationToken, Task> call,
        CancellationToken cancellationToken)
    {
        if (!tokenProvider.IsConfigured)
        {
            logger.LogWarning(
                "Auth0ManagementService: Management API not configured — cannot {Operation} {UserId}", operation, userId);
            return Auth0SyncResult.NotConfigured;
        }

        try
        {
            await call(cancellationToken);
            logger.LogInformation("Auth0ManagementService: {Operation} {UserId} succeeded", operation, userId);
            return Auth0SyncResult.Synced;
        }
        catch (Auth0ManagementTokenException ex)
        {
            logger.LogError(ex,
                "Auth0ManagementService: No Management API token — cannot {Operation} {UserId}", operation, userId);
            return Auth0SyncResult.Failed(Auth0SyncResult.TokenFailedCode);
        }
        catch (RateLimitApiException ex)
        {
            logger.LogError(ex,
                "Auth0ManagementService: Rate limited while trying to {Operation} {UserId}", operation, userId);
            return Auth0SyncResult.Failed(Auth0SyncResult.RateLimitedCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Auth0ManagementService: Failed to {Operation} {UserId} (status {StatusCode})",
                operation, userId, (ex as ErrorApiException)?.StatusCode);
            return Auth0SyncResult.Failed(Auth0SyncResult.ApiErrorCode);
        }
    }

    /// <summary>
    /// Returns the Auth0 ids of <paramref name="roleNames"/>, or null when any of them does not exist.
    /// The full name → id map is cached; a miss triggers one refresh before giving up.
    /// </summary>
    private async Task<string[]?> ResolveRoleIdsAsync(
        IReadOnlyList<string> roleNames,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"auth0:role-ids:{tokenProvider.Domain}";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0 || !memoryCache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, string>? map) || map is null)
            {
                map = await FetchRoleIdsAsync(cancellationToken);
                memoryCache.Set(cacheKey, map, RoleIdCacheDuration);
            }

            var ids = new List<string>(roleNames.Count);
            foreach (var name in roleNames)
            {
                if (!map.TryGetValue(name, out var id))
                    break;
                ids.Add(id);
            }

            if (ids.Count == roleNames.Count)
                return ids.ToArray();
        }

        return null;
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchRoleIdsAsync(CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        const int pageSize = 100;
        for (var page = 0; ; page++)
        {
            var currentPage = page;
            var roles = await ExecuteAsync(
                client => client.Roles.GetAllAsync(
                    new GetRolesRequest(),
                    new PaginationInfo(currentPage, pageSize, false),
                    cancellationToken),
                cancellationToken);

            if (roles is null)
                break;

            foreach (var role in roles)
            {
                if (!string.IsNullOrWhiteSpace(role.Name) && !string.IsNullOrWhiteSpace(role.Id))
                    map[role.Name] = role.Id;
            }

            if (roles.Count < pageSize)
                break;
        }

        return map;
    }

    /// <summary>
    /// Runs <paramref name="call"/> with a fresh client. On HTTP 401 the cached token is dropped and
    /// the call is retried once with a new token.
    /// </summary>
    private async Task<T> ExecuteAsync<T>(Func<ManagementApiClient, Task<T>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var client = await CreateClientAsync(cancellationToken);
            try
            {
                return await call(client);
            }
            catch (ErrorApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                logger.LogWarning("Auth0ManagementService: Management API returned 401 — renewing token");
                tokenProvider.Invalidate();
            }
        }
    }

    private Task ExecuteAsync(Func<ManagementApiClient, Task> call, CancellationToken cancellationToken) =>
        ExecuteAsync<bool>(async client =>
        {
            await call(client);
            return true;
        }, cancellationToken);

    private async Task<ManagementApiClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        var connection = new HttpClientManagementConnection(
            httpClientFactory.CreateClient(Auth0ManagementTokenProvider.HttpClientName));
        return new ManagementApiClient(token, new Uri($"https://{tokenProvider.Domain}/api/v2"), connection);
    }
}
