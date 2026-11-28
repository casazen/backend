using Casazen.Core.SiteProfile;

namespace Casazen.Web.DTOs;

public sealed record PublicAmenityCatalogResponse(
    IReadOnlyList<PublicAmenityGroupDto> Groups,
    IReadOnlyList<PublicAmenityDto> Amenities)
{
    public static PublicAmenityCatalogResponse FromCatalog() => new(
        [.. AmenityCatalog.Groups.Select(g => new PublicAmenityGroupDto(g.Id, new PublicLabelDto(g.LabelIt, g.LabelEn)))],
        [.. AmenityCatalog.Entries.Select(e => new PublicAmenityDto(e.Name, e.Icon, e.Group, new PublicLabelDto(e.LabelIt, e.LabelEn)))]);
}

public sealed record PublicAmenityGroupDto(string Id, PublicLabelDto Label);

public sealed record PublicAmenityDto(string Name, string Icon, string Group, PublicLabelDto Label);

public sealed record PublicLabelDto(string It, string En);
