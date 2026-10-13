namespace Casazen.Core.Services;

/// <summary>
/// Cache of AI answers (A8-25): bounded in size, with a time to live, and keyed by scope, never a static dictionary
/// that grows with every request. <paramref name="orgId"/> is the org the request belongs to (supplier match and
/// discovery, started by a host): its entries are never read by another org and are capped per org. <c>null</c> is the
/// platform scope (public SEO content). A caller that must get a fresh answer (the "regenerate" of an SEO page) does not
/// read the cache and overwrites the entry with <see cref="Set{T}"/>.
/// </summary>
public interface IAiResponseCache
{
    /// <summary>The cached value of <paramref name="key"/> in the scope, when there is a fresh one of type <typeparamref name="T"/>.</summary>
    bool TryGet<T>(Guid? orgId, string key, out T? value) where T : class;

    /// <summary>Stores <paramref name="value"/> (replacing the one of the same key), evicting the oldest entries if a limit is reached.</summary>
    void Set<T>(Guid? orgId, string key, T value) where T : class;

    /// <summary>Entries held now, expired ones not yet removed excluded.</summary>
    int Count { get; }
}
