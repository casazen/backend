using Casazen.Core.Options;
using Casazen.Core.Services;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// The <see cref="IAiResponseCache"/> of the process (singleton): a FIFO of entries that all live the same time, so the
/// oldest is also the first to expire. Bounded three ways (A8-25): <see cref="AiCacheOptions.TtlHours"/>, a total of
/// <see cref="AiCacheOptions.MaxEntries"/> and <see cref="AiCacheOptions.MaxEntriesPerOrg"/> for each org. One lock
/// guards everything: the cache holds a few hundred entries and is touched once per paid AI call.
/// </summary>
public sealed class AiResponseCache(IOptions<AiCacheOptions> options, TimeProvider? timeProvider = null) : IAiResponseCache
{
    private const string PlatformScope = "platform";

    private readonly AiCacheOptions _limits = options.Value.Normalized();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _byAge = new();
    private readonly Dictionary<Guid, int> _perOrg = [];

    public int Count
    {
        get
        {
            lock (_gate)
            {
                PurgeExpired();
                return _index.Count;
            }
        }
    }

    /// <summary>Entries held for <paramref name="orgId"/> now (diagnostics and tests).</summary>
    public int CountForOrg(Guid orgId)
    {
        lock (_gate)
        {
            PurgeExpired();
            return _perOrg.GetValueOrDefault(orgId);
        }
    }

    public bool TryGet<T>(Guid? orgId, string key, out T? value) where T : class
    {
        lock (_gate)
        {
            PurgeExpired();
            if (_index.TryGetValue(Compose(orgId, key), out var node) && node.Value.Value is T typed)
            {
                value = typed;
                return true;
            }
        }

        value = null;
        return false;
    }

    public void Set<T>(Guid? orgId, string key, T value) where T : class
    {
        ArgumentNullException.ThrowIfNull(value);
        var composite = Compose(orgId, key);

        lock (_gate)
        {
            PurgeExpired();

            // A new answer for a key replaces the old one and starts its time again.
            if (_index.TryGetValue(composite, out var existing))
                Remove(existing);

            if (orgId is { } org)
            {
                while (_perOrg.GetValueOrDefault(org) >= _limits.MaxEntriesPerOrg && EvictOldestOf(org))
                {
                }
            }

            while (_index.Count >= _limits.MaxEntries && _byAge.First is { } oldest)
                Remove(oldest);

            var expiresAt = _clock.GetUtcNow().UtcDateTime.AddHours(_limits.TtlHours);
            _index[composite] = _byAge.AddLast(new Entry(composite, orgId, value, expiresAt));
            if (orgId is { } scoped)
                _perOrg[scoped] = _perOrg.GetValueOrDefault(scoped) + 1;
        }
    }

    private static string Compose(Guid? orgId, string key) =>
        $"{(orgId is { } id ? id.ToString("N") : PlatformScope)}|{key}";

    /// <summary>Every entry lives the same time, so the expired ones are the oldest: the front of the list.</summary>
    private void PurgeExpired()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        while (_byAge.First is { } oldest && oldest.Value.ExpiresAt <= now)
            Remove(oldest);
    }

    private bool EvictOldestOf(Guid orgId)
    {
        for (var node = _byAge.First; node is not null; node = node.Next)
        {
            if (node.Value.OrgId != orgId)
                continue;

            Remove(node);
            return true;
        }

        return false;
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _byAge.Remove(node);
        _index.Remove(node.Value.Key);
        if (node.Value.OrgId is not { } org)
            return;

        var left = _perOrg.GetValueOrDefault(org) - 1;
        if (left > 0)
            _perOrg[org] = left;
        else
            _perOrg.Remove(org);
    }

    private sealed record Entry(string Key, Guid? OrgId, object Value, DateTime ExpiresAt);
}
