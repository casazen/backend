using System.Collections.Concurrent;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// CF status cache service with 15-minute expiry and request coalescing (ADR-008-R7 through ADR-008-R10, ADR-008-R14).
/// </summary>
public sealed class ConcordatoFacileStatusCacheService(
    IMemoryCache memoryCache,
    ConcordatoFacileApiClient apiClient,
    AppDbContext db,
    IHostEnvironment environment,
    ILogger<ConcordatoFacileStatusCacheService> logger) : IConcordatoFacileStatusCacheService
{
    private static readonly TimeSpan NormalExpiry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BriefRefreshExpiry = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BriefRefreshWindow = TimeSpan.FromHours(1);

    // Request coalescing: one semaphore per leaseId to prevent concurrent API calls for the same lease
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<CfStatusResult> GetPracticesAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(leaseId, orgId);

        // Check cache: if valid (not expired) and tenant matches, return cached
        if (memoryCache.TryGetValue(cacheKey, out CfStatusSnapshot? snapshot) && snapshot is not null)
        {
            // ADR-008-R10: tenant isolation - verify orgId matches
            if (snapshot.OrgId != orgId)
            {
                logger.LogWarning(
                    "Cached snapshot for lease {LeaseId} belongs to different org {CachedOrgId}, expected {OrgId}",
                    leaseId, snapshot.OrgId, orgId);
                // Fall through to fetch fresh data
            }
            else if (DateTime.UtcNow < snapshot.ExpiresAt)
            {
                return BuildResult(snapshot);
            }
        }

        // Request coalescing: use SemaphoreSlim per leaseId to prevent duplicate API calls
        var lockObj = _locks.GetOrAdd(leaseId, _ => new SemaphoreSlim(1, 1));
        await lockObj.WaitAsync(cancellationToken);
        try
        {
            // Double-check cache after acquiring lock (another request might have fetched it)
            if (memoryCache.TryGetValue(cacheKey, out snapshot) && snapshot is not null &&
                snapshot.OrgId == orgId && DateTime.UtcNow < snapshot.ExpiresAt)
            {
                return BuildResult(snapshot);
            }

            // Fetch fresh data from API
            try
            {
                var practices = await apiClient.GetPracticesByLeaseIdAsync(leaseId, cancellationToken);

                // ADR-008-R9: Brief refresh window - 5-min expiry for 60 min after link emission when practices.Count == 0
                var expiry = await DetermineExpiryAsync(leaseId, practices.Count, cancellationToken);

                // ADR-008-R14: Handle older status - don't overwrite if new data has older statusChangedAt
                if (snapshot is not null && practices.Count > 0)
                {
                    var newLatestStatusChangedAt = practices.Max(p => p.StatusChangedAt);
                    var cachedLatestStatusChangedAt = snapshot.LatestStatusChangedAt;

                    if (cachedLatestStatusChangedAt.HasValue && newLatestStatusChangedAt < cachedLatestStatusChangedAt.Value)
                    {
                        logger.LogInformation(
                            "New data for lease {LeaseId} has older statusChangedAt {NewLatest} than cached {CachedLatest}; keeping cached data",
                            leaseId, newLatestStatusChangedAt, cachedLatestStatusChangedAt.Value);

                        // Refresh expiry but keep the old data
                        snapshot = snapshot with
                        {
                            ExpiresAt = DateTime.UtcNow.Add(expiry),
                            IsStale = false,
                            LastError = null,
                            LastAttemptAt = DateTime.UtcNow
                        };
                        memoryCache.Set(cacheKey, snapshot, new MemoryCacheEntryOptions
                        {
                            SlidingExpiration = expiry
                        });
                        return BuildResult(snapshot);
                    }
                }

                // Cache with 15-min (or 5-min) sliding expiration
                var newSnapshot = new CfStatusSnapshot
                {
                    OrgId = orgId,
                    Practices = practices,
                    FetchedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.Add(expiry),
                    IsStale = false,
                    LastError = null,
                    LastAttemptAt = DateTime.UtcNow
                };

                memoryCache.Set(cacheKey, newSnapshot, new MemoryCacheEntryOptions
                {
                    SlidingExpiration = expiry
                });

                return BuildResult(newSnapshot);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fetch CF practices for lease {LeaseId}", leaseId);

                // ADR-008-R8: On error, retain last valid snapshot as stale
                if (snapshot is not null)
                {
                    snapshot = snapshot with
                    {
                        IsStale = true,
                        LastError = ex.Message,
                        LastAttemptAt = DateTime.UtcNow
                    };

                    // Keep the stale snapshot in cache
                    memoryCache.Set(cacheKey, snapshot, new MemoryCacheEntryOptions
                    {
                        SlidingExpiration = NormalExpiry
                    });

                    return BuildResult(snapshot);
                }

                // No cached snapshot to retain, propagate the error
                throw;
            }
        }
        finally
        {
            lockObj.Release();
        }
    }

    private string BuildCacheKey(Guid leaseId, Guid orgId)
    {
        // ADR-008-R10: Cache key = {env}:{orgId}:{leaseId} for tenant isolation
        return $"{environment.EnvironmentName}:{orgId}:{leaseId}";
    }

    private async Task<TimeSpan> DetermineExpiryAsync(Guid leaseId, int practiceCount, CancellationToken cancellationToken)
    {
        // ADR-008-R9: Brief refresh window - 5-min for 60 min after link emission when practices.Count == 0
        if (practiceCount > 0)
        {
            return NormalExpiry;
        }

        var recentLinkGeneration = await db.LeaseEvents
            .Where(e => e.LeaseContractId == leaseId &&
                        e.EventType == LeaseEventType.ConcordatoFacileLinkGenerated &&
                        e.OccurredAt > DateTime.UtcNow.AddHours(-1))
            .AnyAsync(cancellationToken);

        return recentLinkGeneration ? BriefRefreshExpiry : NormalExpiry;
    }

    private static CfStatusResult BuildResult(CfStatusSnapshot snapshot)
    {
        return new CfStatusResult
        {
            Practices = snapshot.Practices.AsReadOnly(),
            LastCheckedAt = snapshot.FetchedAt,
            NextUpdateAt = snapshot.ExpiresAt,
            IsStale = snapshot.IsStale,
            LastError = snapshot.LastError
        };
    }
}
