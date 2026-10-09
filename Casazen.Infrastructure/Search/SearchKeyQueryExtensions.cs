using Casazen.Core.Search;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Search;

/// <summary>
/// The one place where a query meets the search key of a table (UI-13a). On PostgreSQL the words of the term are a prefix query
/// on the full-text index of the key; where there is no PostgreSQL (the in-memory provider of the unit tests, which has neither
/// generated columns nor full-text search) the same rule is applied in C# to the key the test wrote. Both say: every word of the term
/// is the beginning of a word of the key (<see cref="SearchText.KeyMatches"/>).
/// </summary>
internal static class SearchKeyQueryExtensions
{
    /// <summary>
    /// Keeps the rows whose <c>SearchKey</c> has every word of <paramref name="query"/> at the beginning of one of its words;
    /// no row for an empty query. <paramref name="fullText"/> is true on PostgreSQL.
    /// </summary>
    public static IQueryable<T> WhereKeyMatches<T>(this IQueryable<T> source, SearchQuery query, bool fullText)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(query);

        if (query.IsEmpty)
            return source.Where(_ => false);

        if (fullText)
        {
            // The same expression as the index (to_tsvector('simple', coalesce("SearchKey", '')): the column is nullable in the
            // model, so Npgsql writes the coalesce in the index and the query has to write it too), so that the planner uses the
            // index. The text of the query has only a-z, 0-9, ':*' and '&' (SearchQuery.ToTsQuery): always a valid tsquery.
            var tsQuery = query.ToTsQuery();
            return source.Where(row => EF.Functions
                .ToTsVector(SearchKeyModel.TextSearchConfig, EF.Property<string>(row, SearchKeyModel.KeyProperty) ?? string.Empty)
                .Matches(EF.Functions.ToTsQuery(SearchKeyModel.TextSearchConfig, tsQuery)));
        }

        var tokens = query.Tokens;
        return source.Where(row => SearchText.KeyMatches(EF.Property<string>(row, SearchKeyModel.KeyProperty), tokens));
    }

    /// <summary>The key of a row, for the ordering of the results (alphabetical, as the words are folded).</summary>
    public static IOrderedQueryable<T> OrderByKey<T>(this IQueryable<T> source)
        where T : class =>
        source.OrderBy(row => EF.Property<string>(row, SearchKeyModel.KeyProperty));
}
