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
/// It also keeps the answer of the lookup of a single practice for 15 minutes (<see cref="GetPracticeAsync"/>).
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

    // CF-FIX2: after a failed read the stale snapshot is served for this long before ConcordatoFacile is asked again.
    private static readonly TimeSpan AfterErrorExpiry = TimeSpan.FromMinutes(5);

    // CF-FIX2: a practice ConcordatoFacile does not know yet (the form was sent seconds ago) is asked again soon, not in 15 minutes.
    private static readonly TimeSpan PracticeNotFoundExpiry = TimeSpan.FromMinutes(1);

    // Request coalescing: one semaphore per leaseId to prevent concurrent API calls for the same lease
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public async Task<CfStatusResult> GetPracticesAsync(Guid leaseId, Guid orgId, CancellationToken cancellationToken = default)
    {
        // CF-FIX2 (ADR-008-R11): the lease must be the caller's org's before anything is read from the cache or asked to ConcordatoFacile.
        await db.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

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
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A caller that goes away is not a failure of ConcordatoFacile: it must not hold back the next read for 5 minutes (CF-FIX2).
                logger.LogWarning(ex, "Failed to fetch CF practices for lease {LeaseId}", leaseId);

                // ADR-008-R8: On error, retain last valid snapshot as stale
                if (snapshot is not null)
                {
                    var failedAt = DateTime.UtcNow;
                    snapshot = snapshot with
                    {
                        IsStale = true,
                        LastError = ex.Message,
                        LastAttemptAt = failedAt,

                        // CF-FIX2: without this the stale snapshot would keep its expiry in the past and every opening of the page would
                        // ask ConcordatoFacile again at once; the old data is served as stale until then (and NextUpdateAt says when).
                        ExpiresAt = failedAt.Add(AfterErrorExpiry)
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

    /// <summary>
    /// CF-FIX2: the answer of ConcordatoFacile for a practice number, kept 15 minutes per (environment, practice) so that opening the
    /// upload page again does not read it again (the README promises it). Only ConcordatoFacile's own data is cached and every org shares
    /// it: the lease the practice names is checked against the caller's org each time, outside this cache. A failed read is not cached.
    /// </summary>
    public async Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{environment.EnvironmentName}:practice:{practiceId}";

        if (memoryCache.TryGetValue(cacheKey, out CachedPractice? cached) && cached is not null)
        {
            return cached.Practice;
        }

        var practice = await apiClient.GetPracticeAsync(practiceId, cancellationToken);

        memoryCache.Set(
            cacheKey,
            new CachedPractice(practice),
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = practice is null ? PracticeNotFoundExpiry : NormalExpiry
            });

        return practice;
    }

    private string BuildCacheKey(Guid leaseId, Guid orgId)
    {
        // ADR-008-R10: Cache key = {env}:{orgId}:{leaseId} for tenant isolation
        return $"{environment.EnvironmentName}:{orgId}:{leaseId}";
    }

    /// <summary>What ConcordatoFacile answered for a practice number: the practice, or null when it does not know it.</summary>
    private sealed record CachedPractice(PracticeDto? Practice);

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
