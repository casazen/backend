using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>One mapping of a supplier profile to what the showcase shows, shared by the public page and the owner's preview (SU-13).</summary>
public static class SupplierShowcaseMapper
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <param name="profile">The supplier profile; only the fields the showcase shows are copied.</param>
    /// <param name="listed">The comuni of the profile found in the official list (<see cref="SupplierComuniView.IstatCodes"/>).</param>
    /// <param name="availability">The days to show, already limited to the window and ordered.</param>
    public static SupplierShowcaseDto ToDto(
        SupplierProfile profile,
        IReadOnlyDictionary<string, Comune> listed,
        IEnumerable<(DateOnly Date, bool Available)> availability) => new()
    {
        Slug = profile.ShowcaseSlug,
        LegalName = profile.LegalName,
        Categories = JsonSerializer.Deserialize<string[]>(profile.CategoriesJson, JsonOpts) ?? [],
        // The comuni chosen from the official list are shown by name (SU-04), then what the supplier wrote.
        Comuni = SupplierComuniView.Names(profile, listed),
        Bio = profile.Bio,
        PhotoUrls = JsonSerializer.Deserialize<string[]>(profile.PhotoUrlsJson, JsonOpts) ?? [],
        Availability = availability.Select(a => new AvailabilityEntryDto { Date = a.Date, Available = a.Available }).ToList(),
    };
}
