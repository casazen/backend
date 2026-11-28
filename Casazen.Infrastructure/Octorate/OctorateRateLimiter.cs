using System.Collections.Concurrent;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// The rate limit CasaZen keeps on its own calls to Octorate (ADR-023-R8, R9): a token bucket per org of
/// <see cref="RequestsPerMinute"/> calls, given back <see cref="BurstSize"/> at a time every <see cref="BurstPeriod"/> (100 a minute in
/// bursts of 20), so one org that calls a lot never uses the quota of the others. A call that is not made for an org (the partner-level
/// calls) uses the fixed key <see cref="PlatformKey"/>. When Octorate itself answers 429 the client waits <see cref="BackoffDelays"/>
/// (1, 2 and 4 seconds) and gives up after the third retry.
/// </summary>
/// <remarks>
/// One instance for the application (singleton), in memory: with several API instances each keeps its own buckets, which only makes
/// the limit stricter per instance than Octorate's. In the sandbox runs (170 calls in 127 seconds) Octorate never answered 429.
/// </remarks>
public sealed class OctorateRateLimiter(TimeProvider timeProvider)
{
    public const int RequestsPerMinute = 100;

    public const int BurstSize = 20;

    public const string PlatformKey = "platform";

    public static readonly TimeSpan BurstPeriod = TimeSpan.FromMinutes(1) / (RequestsPerMinute / BurstSize);

    /// <summary>The waits before the retries of a call Octorate answered with 429 (exponential: three retries at most).</summary>
    public static readonly IReadOnlyList<TimeSpan> BackoffDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public Task<bool> TryAcquireAsync(Guid orgId)
    {
        if (orgId == Guid.Empty)
            throw new ArgumentException("A call is counted for an organization or for the platform.", nameof(orgId));

        return Task.FromResult(TryAcquire(KeyOf(orgId)));
    }

    public Task<bool> TryAcquirePlatformAsync() => Task.FromResult(TryAcquire(PlatformKey));

    public int AvailableCalls(Guid orgId)
    {
        if (!_buckets.TryGetValue(KeyOf(orgId), out var bucket))
            return RequestsPerMinute;

        lock (bucket)
        {
            bucket.Refill(timeProvider.GetUtcNow());
            return bucket.Tokens;
        }
    }

    private bool TryAcquire(string key)
    {
        var now = timeProvider.GetUtcNow();
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket(now));
        lock (bucket)
        {
            bucket.Refill(now);
            if (bucket.Tokens <= 0)
                return false;

            bucket.Tokens--;
            return true;
        }
    }

    private static string KeyOf(Guid orgId) => orgId.ToString("N");

    private sealed class Bucket(DateTimeOffset since)
    {
        public int Tokens { get; set; } = RequestsPerMinute;

        private DateTimeOffset Since { get; set; } = since;

        public void Refill(DateTimeOffset now)
        {
            if (now <= Since)
                return;

            var periods = (now - Since).Ticks / BurstPeriod.Ticks;
            if (periods <= 0)
                return;

            Tokens = (int)Math.Min(RequestsPerMinute, Tokens + (periods * BurstSize));
            Since += TimeSpan.FromTicks(periods * BurstPeriod.Ticks);
        }
    }
}
