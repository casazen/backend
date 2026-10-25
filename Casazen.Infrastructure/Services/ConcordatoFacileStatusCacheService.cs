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
/// <para>
/// A status that cannot be read is not an error for the caller (CF-BE, closes CF-FIX2-FU1 and CF-FIX2-FU2): with the status API key
/// not configured ConcordatoFacile is not asked at all, and when it fails with no snapshot to fall back on, the outcome "not available" is
/// kept for five minutes in the same cache, under the same key (environment, org, lease), so the next openings do not ask it again. The
/// answer is then <c>IsAvailable: false</c>, no practices, stale, never checked: an unreadable status is never "no practice".
/// </para>
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

    // CF-FIX2: after a failed read the stale snapshot is served for this long before ConcordatoFacile is asked again. CF-BE: the same
    // five minutes for the outcome "not available" when there is no snapshot.
    private static readonly TimeSpan AfterErrorExpiry = TimeSpan.FromMinutes(5);

    // CF-FIX2: a practice ConcordatoFacile does not know yet (the form was sent seconds ago) is asked again soon, not in 15 minutes.
    private static readonly TimeSpan PracticeNotFoundExpiry = TimeSpan.FromMinutes(1);

    // The status API key is a secret handed over by ConcordatoFacile: the log line says which setting is missing, never a value.
    private const string KeyMissingMessage = "ConcordatoFacile:StatusApiKey not configured; CF status API disabled";

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

            // CF-BE: without the key ConcordatoFacile would answer 401 to everything (and the operators would get an error in the log at
            // every opening of the page): it is not asked. One warning, then the outcome is kept for five minutes.
            if (!apiClient.IsConfigured)
            {
                logger.LogWarning(
                    "{Reason}: the status of lease {LeaseId} is answered as not available for {Minutes} minutes",
                    KeyMissingMessage,
                    leaseId,
                    AfterErrorExpiry.TotalMinutes);
                return RememberUnavailable(cacheKey, orgId, snapshot, "status API key not configured");
            }

            // Fetch fresh data from API
            try
            {
                var practices = await apiClient.GetPracticesByLeaseIdAsync(leaseId, cancellationToken);

                // ADR-008-R9: Brief refresh window - 5-min expiry for 60 min after link emission when practices.Count == 0
                var expiry = await DetermineExpiryAsync(leaseId, practices.Count, cancellationToken);

                // ADR-008-R14: Handle older status - don't overwrite if new data has older statusChangedAt
                if (snapshot is { IsAvailable: true } && practices.Count > 0)
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

                // ADR-008-R8: On error, retain last valid snapshot as stale. CF-BE: and with no snapshot, remember that it is not
                // available instead of failing the request (the next openings do not ask ConcordatoFacile again before five minutes).
                return RememberUnavailable(cacheKey, orgId, snapshot, ex.Message);
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
    /// CF-BE: without the status API key ConcordatoFacile is not asked and the answer is null, like a practice it does not know: the
    /// upload page then falls back to the choice of the contract by hand.
    /// </summary>
    public async Task<PracticeDto?> GetPracticeAsync(string practiceId, CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{environment.EnvironmentName}:practice:{practiceId}";

        if (memoryCache.TryGetValue(cacheKey, out CachedPractice? cached) && cached is not null)
        {
            return cached.Practice;
        }

        if (!apiClient.IsConfigured)
        {
            logger.LogWarning("{Reason}: the practice lookup answers 'not found'", KeyMissingMessage);
            return null;
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

    /// <summary>
    /// The status could not be read (no key, or ConcordatoFacile failed). A snapshot with data is kept as stale and served for five
    /// minutes; with none, the outcome "not available" is stored in its place for five minutes. ConcordatoFacile is not asked again
    /// before then, and <c>NextUpdateAt</c> says when.
    /// </summary>
    private CfStatusResult RememberUnavailable(string cacheKey, Guid orgId, CfStatusSnapshot? previous, string? error)
    {
        var failedAt = DateTime.UtcNow;
        var expiresAt = failedAt.Add(AfterErrorExpiry);

        if (previous is { IsAvailable: true })
        {
            // CF-FIX2: without the new expiry the stale snapshot would keep its own in the past and every opening of the page would ask
            // ConcordatoFacile again at once; the old data is served as stale until then.
            var stale = previous with
            {
                IsStale = true,
                LastError = error,
                LastAttemptAt = failedAt,
                ExpiresAt = expiresAt
            };

            memoryCache.Set(cacheKey, stale, new MemoryCacheEntryOptions
            {
                SlidingExpiration = NormalExpiry
            });

            return BuildResult(stale);
        }

        var unavailable = new CfStatusSnapshot
        {
            OrgId = orgId,
            Practices = [],
            FetchedAt = null,
            ExpiresAt = expiresAt,
            IsStale = true,
            IsAvailable = false,
            LastError = error,
            LastAttemptAt = failedAt
        };

        memoryCache.Set(cacheKey, unavailable, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = AfterErrorExpiry
        });

        return BuildResult(unavailable);
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
            IsAvailable = snapshot.IsAvailable,
            LastError = snapshot.LastError
        };
    }
}
