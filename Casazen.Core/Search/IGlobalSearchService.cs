namespace Casazen.Core.Search;

public interface IGlobalSearchService
{
    /// <summary>
    /// Runs <paramref name="request"/>: at most <see cref="GlobalSearchRequest.Limit"/> results for each group the caller may read,
    /// and only the groups that have one. Never reads a row of another org, nor of a property outside the scope; returns no
    /// document number, phone or full e-mail.
    /// </summary>
    Task<GlobalSearchResult> SearchAsync(GlobalSearchRequest request, CancellationToken cancellationToken = default);
}
