namespace Casazen.Core.DTOs;

/// <summary>
/// Filters of the public property search (<c>GET /api/properties/search</c>, BK-20, A8-13). Every filter is optional and
/// combined with AND; the minimums are inclusive ("at least"). Properties that are not published
/// (<c>PublicListing.IsPublished</c>) or whose org is inactive are never returned, whatever the filters.
/// </summary>
public sealed record PublicPropertySearchCriteria
{
    /// <summary>No filter: every published property.</summary>
    public static PublicPropertySearchCriteria None { get; } = new();

    /// <summary>Part of the city name, case-insensitive.</summary>
    public string? City { get; init; }

    /// <summary>Lowest nightly rate.</summary>
    public decimal? MinPrice { get; init; }

    /// <summary>Highest nightly rate.</summary>
    public decimal? MaxPrice { get; init; }

    /// <summary>At least this many bedrooms.</summary>
    public int? MinBedrooms { get; init; }

    /// <summary>At least this many bathrooms.</summary>
    public int? MinBathrooms { get; init; }

    /// <summary>Sleeps at least this many guests.</summary>
    public int? Guests { get; init; }
}
