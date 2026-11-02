using Casazen.Core.SiteProfile;

namespace Casazen.Web.DTOs;

/// <summary>
/// The reference data of the amenities (<c>GET api/public/amenities</c>, DB-11a): how the booking site names, groups and draws each amenity
/// a property can carry. The same for every site and every property.
/// </summary>
/// <param name="Groups">The groups of the list «Tutti i servizi», in the order they are shown.</param>
/// <param name="Amenities">One entry per amenity, in the order they are listed within their group.</param>
public sealed record PublicAmenityCatalogResponse(
    IReadOnlyList<PublicAmenityGroupDto> Groups,
    IReadOnlyList<PublicAmenityDto> Amenities)
{
    public static PublicAmenityCatalogResponse FromCatalog() => new(
        [.. AmenityCatalog.Groups.Select(g => new PublicAmenityGroupDto(g.Id, new PublicLabelDto(g.LabelIt, g.LabelEn)))],
        [.. AmenityCatalog.Entries.Select(e => new PublicAmenityDto(e.Name, e.Icon, e.Group, new PublicLabelDto(e.LabelIt, e.LabelEn)))]);
}

/// <param name="Id"><c>comfort</c>, <c>kitchen</c>, <c>bedbath</c>, <c>outdoor</c> or <c>safety</c>.</param>
/// <param name="Label">The title of the group in Italian and English.</param>
public sealed record PublicAmenityGroupDto(string Id, PublicLabelDto Label);

/// <param name="Name">The name carried by <c>amenities</c> of the public property DTOs (<c>WiFi</c>, <c>Pool</c>...).</param>
/// <param name="Icon">An icon of the booking site's icon set.</param>
/// <param name="Group">The <see cref="PublicAmenityGroupDto.Id"/> it is listed under.</param>
/// <param name="Label">The label in Italian and English.</param>
public sealed record PublicAmenityDto(string Name, string Icon, string Group, PublicLabelDto Label);

/// <summary>A label in Italian and English.</summary>
public sealed record PublicLabelDto(string It, string En);
