using Casazen.Core.Search;

namespace Casazen.Web.DTOs.Search;

/// <summary>
/// Answer of <c>GET /api/search</c> (UI-13a): the groups that have at least one result, in a fixed order
/// (<see cref="SearchTypes.All"/>). A group the caller may not read is never in it, whether or not it has matching rows.
/// </summary>
public sealed record GlobalSearchResponse(IReadOnlyList<SearchGroupDto> Groups)
{
    public static GlobalSearchResponse From(GlobalSearchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new GlobalSearchResponse(result.Groups.Select(SearchGroupDto.From).ToList());
    }
}

/// <summary>The results of one kind. <c>hasMore</c> is true when there are more than the <c>limit</c> asked for.</summary>
public sealed record SearchGroupDto(string Type, bool HasMore, IReadOnlyList<SearchHitDto> Items)
{
    public static SearchGroupDto From(SearchGroup group) =>
        new(group.Type, group.HasMore, group.Items.Select(SearchHitDto.From).ToList());
}

/// <summary>
/// One result: only a kind, an id, a short title, a line that tells it apart and where it opens. Nothing sensitive: a guest comes with
/// its name and its e-mail masked, never the phone, the document, the address or the dates of birth.
/// </summary>
/// <param name="Type">The kind (<c>property</c>, <c>booking</c>, <c>guest</c>, <c>lease</c>, <c>service-request</c>, <c>supplier</c>, <c>supplier-request</c>).</param>
/// <param name="Id">The id of the object, for the route of <paramref name="Destination"/>.</param>
/// <param name="Title">A short title: a name, a code, a service.</param>
/// <param name="Subtitle">A line that tells it apart from the others, or null.</param>
/// <param name="Destination">A key such as <c>short-rent.booking</c>: the area and the kind. Not a URL; the client maps it to its route.</param>
public sealed record SearchHitDto(string Type, Guid Id, string Title, string? Subtitle, string Destination)
{
    public static SearchHitDto From(SearchHit hit) => new(hit.Type, hit.Id, hit.Title, hit.Subtitle, hit.Destination);
}
