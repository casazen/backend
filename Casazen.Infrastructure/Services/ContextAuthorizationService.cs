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
/// <remarks>
/// The host contexts (short-rent, long-rent) are withheld until the host onboarding is complete, whatever the JWT
/// roles, the memberships or the DB role say (PL-02, A1-05, <see cref="HostOnboarding"/>): a user registered on Auth0
/// who never went through the onboarding and its consents gets no host permission, from the web, the app or the API.
/// </remarks>
public class ContextAuthorizationService(
    IUserAuthorizationSnapshotStore snapshotStore,
    ILegalDocumentService legalDocuments,
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
        // AM-01: a deactivated member of an org reaches nothing (the request itself is refused earlier with 403
        // member_inactive; this keeps any other reader of the snapshot from granting meanwhile).
        if (snapshot.IsOrgMemberDeactivated)
            return [];

        var contexts = MergeContexts(snapshot);
        if (HostOnboardingGate.Evaluate(snapshot, legalDocuments).IsComplete)
            return contexts;

        // PL-02: no host context before the onboarding and the current consents. Admin and supplier stay.
        return contexts.Where(c => !HostOnboarding.IsHostContext(c.ContextKey)).ToList();
    }

    /// <remarks>
    /// <para>The DB memberships are the source; the JWT roles only complete them. <b>Veto (AM-01, S3):</b> for a user who
    /// belongs to an org (it has an <see cref="OrgMember"/>) and holds a rental membership in the DB (short-rent or
    /// long-rent), the host contexts come from the DB only: a JWT role never adds a missing host context, so a former
    /// owner of a context (who switched rental type, moved to another org...) keeps nothing of it with an old token
    /// until it expires. <c>admin</c> and <c>supplier</c> are not host contexts and still complete from the token (the
    /// staff and the supplier link are not org matters). Same rule as the open PR #455 ("host context memberships
    /// authoritative"), limited here to org members: for everyone else the token still completes the contexts, so
    /// nothing changes for users that are in no org team.</para>
    /// <para>The veto starts with the first rental membership: a member whose DB holds none (an owner whose rental row
    /// was never written, a test database without the seeded roles) has no host data to be authoritative about, so the
    /// token still completes and nobody is locked out of the contexts they had before. Every member added by
    /// <see cref="IOrgMembershipService"/> has at least one, so for them the DB is the whole truth.</para>
    /// </remarks>
    private IReadOnlyList<ContextAccess> MergeContexts(UserAuthorizationSnapshot snapshot)
    {
        var memberships = snapshot.Memberships;
        var jwtRoles = ResolveJwtRoles();

        if (jwtRoles.Count == 0 && memberships.Count == 0 && snapshot.Exists)
        {
            jwtRoles = MapDbUserRoleToJwtRoles(snapshot.Role);
        }

        var jwtContexts = ContextAccessBootstrap.BuildFallbackAccess(jwtRoles);
        if (snapshot.OrgMember is not null && memberships.Any(m => OrgRoleCatalog.IsRentalContext(m.ContextKey)))
        {
            jwtContexts = jwtContexts.Where(c => !HostOnboarding.IsHostContext(c.ContextKey)).ToList();
        }

        if (memberships.Count > 0)
        {
            var fromDb = memberships.ToList();

            var existingKeys = fromDb.Select(c => c.ContextKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var jwtContext in jwtContexts)
            {
                if (!existingKeys.Contains(jwtContext.ContextKey))
                {
                    fromDb.Add(jwtContext);
                }
            }

            return fromDb.OrderBy(c => c.ContextKey, StringComparer.OrdinalIgnoreCase).ToList();
        }

        return jwtContexts;
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
                logger.LogDebug(
                    "Permission denied: user {UserId} has no context {ContextKey} (host contexts wait for the onboarding)",
                    userId, contextKey);
                return false;
            }

            if (string.IsNullOrWhiteSpace(permissionKey))
            {
                return true;
            }

            // A permission counts only in the context that grants it: long-rent property.* never satisfies a
            // short-rent policy. Endpoints shared by both rental contexts say so in their policy (A7-06).
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

    /// <remarks>
    /// The fallback when the token carries no role and the user has no membership. Only the owner roles map to a
    /// context: a <c>PropertyManager</c> used to map to the <c>PropertyOwner</c> contexts, so a property manager whose
    /// token carried no role (the Auth0 role sync failed) became the owner of the org (AM-01, follow-up of AM-00). A
    /// property manager gets its contexts from its memberships (<c>property_manager</c>), never from this mapping.
    /// </remarks>
    private static IReadOnlyList<string> MapDbUserRoleToJwtRoles(UserRole role) =>
        role switch
        {
            UserRole.Admin => ["Admin"],
            UserRole.PropertyOwner => ["PropertyOwner"],
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
            AccountContext.Key => AccountContext.DefaultRoute,
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
