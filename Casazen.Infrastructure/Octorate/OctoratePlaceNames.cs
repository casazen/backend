using Casazen.Core.Octorate;
using Casazen.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace Casazen.Infrastructure.Octorate;

public interface IOctoratePlaceNames
{
    /// <summary>
    /// The table of the places of <paramref name="country"/> (two-letter code), for the org of <see cref="IOctorateOrgScope"/>. Kept for
    /// <see cref="OctoratePlaceNames.CacheDuration"/>: only the first call of a country in a day reads it from Octorate. A table that cannot be
    /// read is the typed error of the client (never an empty table), so the caller records a failure and the write is tried again.
    /// </summary>
    Task<OctoratePlaceTable> GetPlacesAsync(string country, CancellationToken ct);
}

/// <inheritdoc cref="IOctoratePlaceNames" />
/// <remarks>
/// <para><b>One table for everybody.</b> The table is platform data, the same for every org: it is cached per country, not per org (the first
/// call of the day acts for the org that needed it, with its token and its bucket). It is a memory cache, never a table of the database: a
/// restart reads it again.</para>
/// <para><b>One read at a time.</b> Two sends that find the cache empty do not both download it (about 11,000 places): the second waits for the first.</para>
/// <para>Nothing personal passes here: the table names places, and no guest is asked about.</para>
/// </remarks>
public sealed class OctoratePlaceNames(IOctorateApiClient client, IMemoryCache cache) : IOctoratePlaceNames
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly SemaphoreSlim ReadGate = new(1, 1);

    public async Task<OctoratePlaceTable> GetPlacesAsync(string country, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        var key = CacheKey(country);
        if (cache.TryGetValue(key, out OctoratePlaceTable? cached) && cached is not null)
            return cached;

        await ReadGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out cached) && cached is not null)
                return cached;

            var table = new OctoratePlaceTable(await client.GetCheckInCitiesAsync(country, ct));
            cache.Set(key, table, CacheDuration);
            return table;
        }
        finally
        {
            ReadGate.Release();
        }
    }

    internal static string CacheKey(string country) => $"octorate-checkin-places:{country.Trim().ToUpperInvariant()}";
}
