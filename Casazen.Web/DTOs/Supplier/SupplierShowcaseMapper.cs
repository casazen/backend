using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Supplier;

public static class SupplierShowcaseMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static bool HasPublicContent(SupplierProfile profile) =>
        (JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? []).Length > 0
        || !string.IsNullOrWhiteSpace(profile.Bio);

    public static SupplierShowcaseDto ToDto(
        SupplierProfile profile,
        IReadOnlyDictionary<string, Comune> listed,
        IEnumerable<(DateOnly Date, bool Available)> availability) => new()
        {
            Slug = profile.ShowcaseSlug,
            LegalName = profile.LegalName,
            Categories = JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? [],
            Comuni = SupplierComuniView.Names(profile, listed),
            Bio = profile.Bio,
            PhotoUrls = JsonSerializer.Deserialize<string[]>(profile.PhotoUrlsJson, JsonOpts) ?? [],
            Availability = availability.Select(a => new AvailabilityEntryDto { Date = a.Date, Available = a.Available }).ToList(),
        };
}
