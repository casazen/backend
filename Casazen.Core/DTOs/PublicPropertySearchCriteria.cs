namespace Casazen.Core.DTOs;

/// <summary>
/// Filters of the public property search (<c>GET /api/properties/search</c>, BK-20, A8-13). Every filter is optional and
/// combined with AND; the minimums are inclusive ("at least"). Properties that are not published
/// (<c>PublicListing.IsPublished</c>) or whose org is inactive are never returned, whatever the filters.
/// </summary>
public sealed record PublicPropertySearchCriteria
{
    public static PublicPropertySearchCriteria None { get; } = new();

    public string? City { get; init; }

    public decimal? MinPrice { get; init; }

    public decimal? MaxPrice { get; init; }

    public int? MinBedrooms { get; init; }

    public int? MinBathrooms { get; init; }

    public int? Guests { get; init; }
}
