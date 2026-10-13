namespace Casazen.Core.Services;

/// <summary>
/// A property listed on the SEO page of its comune (SE-04, #300 AC2): what the card needs and what builds the link to the
/// host's booking site (<c>/book/{OrgSlug}/property/{Slug ?? Id}</c>). Only what the public booking site already shows.
/// </summary>
public sealed record SeoFeaturedPropertyDto(
    Guid Id,
    string? Slug,
    string OrgSlug,
    string Name,
    string City,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    decimal NightlyRate,
    string? PhotoUrl);

/// <summary>The published properties of a comune, with the comune as CasaZen knows it.</summary>
public sealed record SeoFeaturedPropertiesDto(
    string ComuneSlug,
    string ComuneName,
    IReadOnlyList<SeoFeaturedPropertyDto> Properties);

/// <summary>Featured properties of the comune of an SEO page (SE-04, #300 AC2, A8-10).</summary>
public interface ISeoFeaturedPropertiesService
{
    /// <summary>Most properties one page lists.</summary>
    public const int MaxProperties = 6;

    /// <summary>
    /// Published properties (<see cref="PublicListing.IsPublished"/>: active, not paused, compliance activated) of the
    /// comune of <paramref name="comune"/> (slug or ISTAT code), of active orgs, newest first, at most
    /// <see cref="MaxProperties"/>. <c>null</c> when CasaZen does not know the comune.
    /// </summary>
    Task<SeoFeaturedPropertiesDto?> GetAsync(string comune, CancellationToken cancellationToken = default);
}
