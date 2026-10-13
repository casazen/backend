using Microsoft.Extensions.Caching.Memory;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The hosts that resolved to no org (<see cref="PublicHostResolver"/>), remembered for a short time (BK-16). The CORS
/// check asks about the origin of every foreign request, so without this a flood of made-up hosts would run two database
/// lookups each. The cache is bounded (<see cref="Capacity"/> entries): when it is full new entries are simply not
/// remembered, so an attacker can neither grow the memory nor push the real hosts out (those live in the other cache).
/// A singleton shared by all requests.
/// </summary>
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
