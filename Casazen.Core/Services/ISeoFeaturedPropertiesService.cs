namespace Casazen.Core.Services;

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

public sealed record SeoFeaturedPropertiesDto(
    string ComuneSlug,
    string ComuneName,
    IReadOnlyList<SeoFeaturedPropertyDto> Properties);

public interface ISeoFeaturedPropertiesService
{
    public const int MaxProperties = 6;

    /// <summary>
    /// Published properties (<see cref="PublicListing.IsPublished"/>: active, not paused, compliance activated) of the
    /// comune of <paramref name="comune"/> (slug or ISTAT code), of active orgs, newest first, at most
    /// <see cref="MaxProperties"/>. <c>null</c> when CasaZen does not know the comune.
    /// </summary>
    Task<SeoFeaturedPropertiesDto?> GetAsync(string comune, CancellationToken cancellationToken = default);
}
