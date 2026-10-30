using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

/// <summary>What the host says of one level of the floor plan.</summary>
/// <param name="Name">The name in Italian (required) and English (optional).</param>
/// <param name="Level">The floor, <see cref="SitePropertyProfileLimits.MinFloorLevel"/> to <see cref="SitePropertyProfileLimits.MaxFloorLevel"/>, 0 = ground floor; <c>null</c> = 0.</param>
/// <param name="AreaSqm">The living area of the level, <see cref="Entities.PropertyFloorPlan.MinAreaSqm"/> to <see cref="Entities.PropertyFloorPlan.MaxAreaSqm"/>; <c>null</c> = not said.</param>
public sealed record FloorPlanInput(LocalizedTextInput? Name, int? Level, int? AreaSqm);

/// <summary>Limits and codes of the floor plan images (DB-11a).</summary>
public static class FloorPlanLimits
{
    /// <summary>Largest accepted image, in bytes (5 MB): a plan is a drawing, not a photograph.</summary>
    public const long MaxFileSizeBytes = 5 * 1024 * 1024;

    /// <summary>Content types of the accepted images; each file is also checked on its content.</summary>
    public static readonly IReadOnlyList<string> AllowedContentTypes = ["image/png", "image/jpeg"];

    public const string NoFileCode = "property_floor_plan_none";
    public const string InvalidTypeCode = "property_floor_plan_invalid_type";
    public const string InvalidSizeCode = "property_floor_plan_invalid_size";
    public const string LimitReachedCode = "property_floor_plan_limit_reached";
    public const string NotFoundCode = "property_floor_plan_not_found";
    public const string AreaInvalidCode = "property_floor_plan_area_invalid";
}

/// <summary>
/// The floor plan of a property (DB-11a): at most <see cref="Entities.PropertyFloorPlan.MaxPerProperty"/> levels, each a PNG or JPEG image the
/// host uploads with a name. Reads and writes the property of the caller's org (the tenant filter hides another org's one); the permission is the
/// caller's to check. Changes take the property's media lock, so two uploads at once never pass the limit.
/// </summary>
public interface IPropertyFloorPlanService
{
    /// <summary>The levels, lowest floor first; <c>null</c> when the property does not exist for the caller.</summary>
    Task<IReadOnlyList<PublicFloorPlanDto>?> ListAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Uploads a level. Nothing is stored when anything is wrong. <c>null</c> when the property does not exist for the caller.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>property_floor_plan_none</c> / <c>_invalid_type</c> / <c>_invalid_size</c> / <c>_limit_reached</c> / <c>_area_invalid</c>, or
    /// <c>property_site_profile_italian_required</c> / <c>_text_too_long</c> / <c>_floor_level_invalid</c> with the <c>field</c>.
    /// </exception>
    Task<PublicFloorPlanDto?> AddAsync(Guid propertyId, IFormFile? image, FloorPlanInput input, CancellationToken cancellationToken = default);

    /// <summary>Changes the name, the floor and the area of a level (the image stays). <c>null</c> when the property does not exist for the caller.</summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_floor_plan_not_found</c>.</exception>
    Task<PublicFloorPlanDto?> UpdateAsync(Guid propertyId, Guid planId, FloorPlanInput input, CancellationToken cancellationToken = default);

    /// <summary>Removes a level and its image. <c>false</c> when the property does not exist for the caller.</summary>
    /// <exception cref="Exceptions.NotFoundException"><c>property_floor_plan_not_found</c>.</exception>
    Task<bool> DeleteAsync(Guid propertyId, Guid planId, CancellationToken cancellationToken = default);
}

/// <summary>One point of interest as the host form sends it.</summary>
/// <param name="Name">Italian (required) and English (optional).</param>
/// <param name="Minutes">Minutes to get there, <see cref="Entities.PropertyPoi.MinMinutes"/> to <see cref="Entities.PropertyPoi.MaxMinutes"/>.</param>
/// <param name="DistanceMeters">The distance the host states; stored rounded and not closer than <see cref="Entities.PropertyPoi.MinDistanceMeters"/> (<see cref="PoiDistance"/>).</param>
public sealed record PoiInput(
    PoiKind Kind,
    LocalizedTextInput? Name,
    PoiTravelMode Mode,
    int Minutes,
    CompassPoint Direction,
    int DistanceMeters);

/// <summary>Codes of the points of interest (DB-11a).</summary>
public static class PoiCodes
{
    public const string LimitReachedCode = "property_poi_limit_reached";
    public const string MinutesInvalidCode = "property_poi_minutes_invalid";
}

/// <summary>
/// The points of interest around a property (DB-11a). The host sends the whole list and it replaces the stored one; nothing is worked out from
/// the position of the property.
/// </summary>
public interface IPropertyPoiService
{
    /// <summary>The points in the order the host gave; <c>null</c> when the property does not exist for the caller.</summary>
    Task<IReadOnlyList<PublicPoiDto>?> ListAsync(Guid propertyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the list (an empty list removes every point) and answers with what is stored; <c>null</c> when the property does not exist for the
    /// caller.
    /// </summary>
    /// <exception cref="Exceptions.DomainRuleException">
    /// 422 <c>property_poi_limit_reached</c>, <c>property_poi_minutes_invalid</c>, or <c>property_site_profile_italian_required</c> /
    /// <c>_text_too_long</c> with <c>field: pois</c>. Nothing is saved.
    /// </exception>
    Task<IReadOnlyList<PublicPoiDto>?> ReplaceAsync(Guid propertyId, IReadOnlyList<PoiInput> pois, CancellationToken cancellationToken = default);
}
