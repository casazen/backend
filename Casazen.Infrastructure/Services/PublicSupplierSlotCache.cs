using System.Collections.Concurrent;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The slots of a service from today to the supplier's horizon, as the public sees them, computed once (SP-09): what
/// <see cref="PublicSupplierSlotCache"/> keeps. No reason of closure, no kind, no label: only the days and their free slots.
/// </summary>
/// <param name="BookableUntil">The last Europe/Rome day that can be booked (today plus the supplier's horizon).</param>
/// <param name="Days">Every day from today to <paramref name="BookableUntil"/>, in date order.</param>
public sealed record PublicSlotPlan(DateOnly BookableUntil, IReadOnlyList<PublicSlotDay> Days);

/// <summary>
/// The short cache of the public slots (SP-09, gap report §4.1): the plan of a service of a supplier is computed at most once
/// every <see cref="PublicShowcaseLimits.SlotsCacheTtl"/> (30 seconds) per replica, however many visitors read it. <b>A slot
/// shown is not a promise</b>: a booking recomputes the slot under the supplier's calendar lock (SP-10), and a change of the
/// supplier's agenda reaches the public within the TTL.
/// </summary>
/// <remarks>
/// <para>One plan per (supplier, service, its planning terms, today): the key is made of a service that exists and belongs to
/// an active supplier, never of what the client sent, so a client cannot make up keys. The query string only <i>slices</i>
/// the plan. The cache is also bounded (<see cref="PublicShowcaseLimits.SlotsCacheCapacity"/>): when it is full the expired
/// entries go first, and if it is still full a new plan is simply not remembered, so memory never grows and no entry is
/// pushed out. In memory only, per replica: after a restart or on another replica the plan is computed again, never wrong.</para>
/// <para>A singleton (the plan is not tied to a request); time comes from the injected <see cref="TimeProvider"/>.</para>
/// </remarks>
public sealed class PublicSupplierSlotCache(TimeProvider timeProvider, TimeSpan? ttl = null, int? capacity = null)
{
    private readonly TimeSpan _ttl = ttl ?? PublicShowcaseLimits.SlotsCacheTtl;
    private readonly int _capacity = capacity ?? PublicShowcaseLimits.SlotsCacheCapacity;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Entries held, expired ones not yet purged included (diagnostics and tests).</summary>
    public int Count => _entries.Count;

    /// <summary>The plan under <paramref name="key"/> when it is still fresh.</summary>
    public bool TryGet(string key, out PublicSlotPlan plan)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > Now())
            {
                plan = entry.Plan;
                return true;
            }

            // Only this very entry: a fresher one written meanwhile stays.
            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }

        plan = null!;
        return false;
    }

    /// <summary>Remembers <paramref name="plan"/> under <paramref name="key"/> for the TTL, unless the cache is full.</summary>
    public void Set(string key, PublicSlotPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var now = Now();
        if (_entries.Count >= _capacity)
            PurgeExpired(now);

        // Full of fresh plans: this one is not remembered (a replacement of an existing key is always fine).
        if (_entries.Count >= _capacity && !_entries.ContainsKey(key))
            return;

        _entries[key] = new Entry(plan, now + _ttl);
    }

    private DateTimeOffset Now() => timeProvider.GetUtcNow();

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }
    }

    private sealed record Entry(PublicSlotPlan Plan, DateTimeOffset ExpiresAt);
}
