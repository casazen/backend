using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;
using Microsoft.AspNetCore.Http;

namespace Casazen.Core.Services;

public sealed record FloorPlanInput(LocalizedTextInput? Name, int? Level, int? AreaSqm);

public static class FloorPlanLimits
{
    public const long MaxFileSizeBytes = 5 * 1024 * 1024;

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
    Task<IReadOnlyList<PublicFloorPlanDto>?> ListAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<PublicFloorPlanDto?> AddAsync(Guid propertyId, IFormFile? image, FloorPlanInput input, CancellationToken cancellationToken = default);

    Task<PublicFloorPlanDto?> UpdateAsync(Guid propertyId, Guid planId, FloorPlanInput input, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid propertyId, Guid planId, CancellationToken cancellationToken = default);
}

public sealed record PoiInput(
    PoiKind Kind,
    LocalizedTextInput? Name,
    PoiTravelMode Mode,
    int Minutes,
    CompassPoint Direction,
    int DistanceMeters);

public static class PoiCodes
{
    public const string LimitReachedCode = "property_poi_limit_reached";
    public const string MinutesInvalidCode = "property_poi_minutes_invalid";
}

public interface IPropertyPoiService
{
    Task<IReadOnlyList<PublicPoiDto>?> ListAsync(Guid propertyId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PublicPoiDto>?> ReplaceAsync(Guid propertyId, IReadOnlyList<PoiInput> pois, CancellationToken cancellationToken = default);
}
