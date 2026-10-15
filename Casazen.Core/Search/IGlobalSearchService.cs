namespace Casazen.Core.Search;

/// <summary>
/// The global search (UI-13a, <c>GET /api/search</c>): finds, among the objects of the caller, the ones whose words begin with the
/// words of the term. The service decides nothing about permissions: the web layer says which groups the caller may read
/// (<see cref="HostSearchAccess"/>, <see cref="GlobalSearchRequest.SupplierOrgId"/>) with the policies of the endpoints, and the
/// service reads only those, in SQL, bound to the org and to the properties the scope reaches (<c>InScope</c>). Strategy, indexes
/// and what each group matches: <c>docs/runbooks/global-search.md</c>.
/// </summary>
public interface IGlobalSearchService
{
    /// <summary>
    /// Runs <paramref name="request"/>: at most <see cref="GlobalSearchRequest.Limit"/> results for each group the caller may read,
    /// and only the groups that have one. Never reads a row of another org, nor of a property outside the scope; returns no
    /// document number, phone or full e-mail.
    /// </summary>
    Task<GlobalSearchResult> SearchAsync(GlobalSearchRequest request, CancellationToken cancellationToken = default);
}
