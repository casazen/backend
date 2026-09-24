using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// DB-side authorization data of a user, read once and shared by the JWT supplier backfill, the
/// context policies and the host onboarding gate (PL-02).
/// </summary>
/// <param name="OrgId">The user's org (<c>User.OrgId</c>): the consents below are the ones recorded for it.</param>
/// <param name="OnboardingCompletedAt">When the user completed the onboarding; <c>null</c> when never.</param>
/// <param name="AcceptedConsents">
/// Consents recorded by the user for <paramref name="OrgId"/>, as <see cref="ConsentKey"/> values (type and version):
/// the gate compares them with the current versions when it evaluates, so a new document version applies at once.
/// </param>
public sealed record UserAuthorizationSnapshot(
    bool Exists,
    bool IsActive,
    UserRole Role,
    Guid? SupplierOrgId,
    IReadOnlyList<ContextAccess> Memberships,
    Guid? OrgId = null,
    DateTime? OnboardingCompletedAt = null,
    IReadOnlySet<string>? AcceptedConsents = null)
{
    public static UserAuthorizationSnapshot Missing { get; } =
        new(Exists: false, IsActive: true, Role: UserRole.None, SupplierOrgId: null, Memberships: []);

    /// <summary>Key of an accepted consent in <see cref="AcceptedConsents"/>.</summary>
    public static string ConsentKey(ConsentType type, string version) => $"{type}:{version}";

    /// <summary>True when the user accepted <paramref name="type"/> at <paramref name="version"/> for the current org.</summary>
    public bool HasAccepted(ConsentType type, string version) =>
        AcceptedConsents?.Contains(ConsentKey(type, version)) == true;
}

public interface IUserAuthorizationSnapshotStore : IUserAuthorizationCache
{
    Task<UserAuthorizationSnapshot> GetAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Two-level cache for <see cref="UserAuthorizationSnapshot"/>: per request (<c>HttpContext.Items</c>,
/// so repeated policy evaluations in one request never hit the DB twice) and process-wide
/// (<see cref="IMemoryCache"/>, <c>Authorization:UserCacheSeconds</c>, default 60 s, 0 disables it).
/// Writers of role, membership, supplier link, active flag, onboarding or consents call <see cref="Invalidate"/>.
/// With several API instances the other instances converge within the cache duration.
/// </summary>
public sealed class UserAuthorizationSnapshotStore(
    AppDbContext db,
    IMemoryCache memoryCache,
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration) : IUserAuthorizationSnapshotStore
{
    public const int DefaultCacheSeconds = 60;
    private const string KeyPrefix = "casazen:authz:user:";

    public async Task<UserAuthorizationSnapshot> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var key = KeyPrefix + userId;
        var items = httpContextAccessor.HttpContext?.Items;
        if (items is not null && items.TryGetValue(key, out var perRequest) && perRequest is UserAuthorizationSnapshot hit)
            return hit;

        var ttl = CacheDuration;
        if (ttl <= TimeSpan.Zero || !memoryCache.TryGetValue(key, out UserAuthorizationSnapshot? snapshot) || snapshot is null)
        {
            snapshot = await LoadAsync(userId, cancellationToken);
            if (ttl > TimeSpan.Zero)
                memoryCache.Set(key, snapshot, ttl);
        }

        if (items is not null)
            items[key] = snapshot;

        return snapshot;
    }

    public void Invalidate(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return;

        var key = KeyPrefix + userId;
        memoryCache.Remove(key);
        httpContextAccessor.HttpContext?.Items.Remove(key);
    }

    private TimeSpan CacheDuration
    {
        get
        {
            var seconds = configuration.GetValue("Authorization:UserCacheSeconds", DefaultCacheSeconds);
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
    }

    private async Task<UserAuthorizationSnapshot> LoadAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.Role, u.SupplierOrgId, u.OrgId, u.OnboardingCompletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (user is null)
            return UserAuthorizationSnapshot.Missing;

        var memberships = await db.UserContextMemberships
            .AsNoTracking()
            .Include(m => m.Context)
            .Include(m => m.Role)
            .ThenInclude(r => r.Permissions)
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.ContextKey)
            .ToListAsync(cancellationToken);

        var access = memberships
            .Select(m => new ContextAccess(
                m.ContextKey,
                m.Context.DisplayName,
                m.Role.RoleKey,
                m.Role.Permissions.Select(p => p.PermissionKey).OrderBy(p => p).ToList(),
                ContextAuthorizationService.GetDefaultRoute(m.ContextKey)))
            .ToList();

        // Consents of the user's current org only (PL-02). IgnoreQueryFilters: the snapshot is read while the JWT is
        // validated, before the request tenant is resolved (the tenant filter would match nothing); the query is
        // scoped explicitly to this user and this user's own org.
        var acceptedConsents = new HashSet<string>(StringComparer.Ordinal);
        if (user.OrgId is Guid orgId)
        {
            var consents = await db.ConsentRecords
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.UserId == userId && c.OrgId == orgId)
                .Select(c => new { c.Type, c.Version })
                .Distinct()
                .ToListAsync(cancellationToken);
            foreach (var consent in consents)
                acceptedConsents.Add(UserAuthorizationSnapshot.ConsentKey(consent.Type, consent.Version));
        }

        return new UserAuthorizationSnapshot(
            true,
            user.IsActive,
            user.Role,
            user.SupplierOrgId,
            access,
            user.OrgId,
            user.OnboardingCompletedAt,
            acceptedConsents);
    }
}
