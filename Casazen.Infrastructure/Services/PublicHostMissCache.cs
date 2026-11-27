using Microsoft.Extensions.Caching.Memory;

namespace Casazen.Infrastructure.Services;

public sealed class PublicHostMissCache : IDisposable
{
    /// <summary>Most hosts remembered at the same time.</summary>
    public const int Capacity = 10_000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = Capacity });

    public bool Contains(string normalizedHost) => _cache.TryGetValue(normalizedHost, out _);

    public void Add(string normalizedHost, TimeSpan ttl) =>
        _cache.Set(normalizedHost, true, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });

    public void Remove(string normalizedHost) => _cache.Remove(normalizedHost);

    public void Dispose() => _cache.Dispose();
}
