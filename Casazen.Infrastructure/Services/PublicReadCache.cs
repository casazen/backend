using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// A small, bounded, short-lived cache for the heavy anonymous reads of the booking site (DB-07a): the search of a site and its calendar,
/// asked again and again with the same dates by every visitor of the home. It keeps an answer for <c>PublicRead:CacheSeconds</c> seconds
/// (30 by default; 0 turns it off) and at most <see cref="MaxEntries"/> answers, so a client that varies the query cannot grow it: when it is
/// full a new answer is simply not kept. Nothing invalidates it: an answer is at most that old (it says so in <c>generatedAt</c>), and the
/// quote and the checkout never read it, so a price or a night changed meanwhile can only make a list stale, never a booking wrong.
/// </summary>
/// <remarks>
/// One computation per key at a time within a replica (the concurrent callers share the task of the first); a computation that fails is not
/// kept. The factory of a cached read must not use the token of the request that happens to start it: the answer is shared.
/// </remarks>
public sealed class PublicReadCache : IDisposable
{
    public const string SecondsSetting = "PublicRead:CacheSeconds";

    public const int DefaultSeconds = 30;

    public const int MaxEntries = 256;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    private readonly TimeSpan _lifetime;

    public PublicReadCache(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _lifetime = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue(SecondsSetting, DefaultSeconds)));
    }

    /// <summary>The answer kept under <paramref name="key"/>, or the one <paramref name="factory"/> works out (and keeps).</summary>
    public Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
        where T : class
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (_lifetime <= TimeSpan.Zero)
            return factory();

        var entry = _cache.GetOrCreate(key, cacheEntry =>
        {
            cacheEntry.SetSize(1);
            cacheEntry.AbsoluteExpirationRelativeToNow = _lifetime;
            return new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        })!;

        var task = entry.Value;
        if (!task.IsCompletedSuccessfully)
        {
            // A failed or cancelled computation must not be served for the rest of the lifetime.
            _ = task.ContinueWith(
                _ => _cache.Remove(key),
                CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return task;
    }

    public void Dispose() => _cache.Dispose();
}
