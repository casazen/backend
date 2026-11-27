using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;
using Microsoft.AspNetCore.Http;

namespace Casazen.Web.DTOs;

public sealed class SitePropertyProfileRequest
{
    private const string TooLong = "PropertySiteProfilePayloadTooLong";

    public int? FloorLevel { get; set; }

    public int? RenovatedYear { get; set; }

    [MaxLength(16, ErrorMessage = TooLong)]
    public string? EnergyClass { get; set; }

    public decimal? EnergyIndex { get; set; }

    public PropertyAreaKind? AreaKind { get; set; }

    public bool? ChildrenWelcome { get; set; }

    public TimeOnly? CheckInTo { get; set; }

    [MaxLength(2000, ErrorMessage = TooLong)]
    public string? VideoUrl { get; set; }

    [MaxLength(2000, ErrorMessage = TooLong)]
    public string? TourUrl { get; set; }

    public SiteTextRequest? Zone { get; set; }

    public SiteTextRequest? Near { get; set; }

    public SiteTextRequest? FloorNote { get; set; }

    public SiteTextRequest? OutdoorNote { get; set; }

    public SiteTextRequest? CheckInMethod { get; set; }

    public SiteTextRequest? RulesNote { get; set; }

    [MaxLength(20, ErrorMessage = TooLong)]
    public List<SiteHighlightRequest>? Highlights { get; set; }

    [MaxLength(50, ErrorMessage = TooLong)]
    public List<SiteTextRequest>? SafetyNotes { get; set; }

    [MaxLength(50, ErrorMessage = TooLong)]
    public List<string>? Tags { get; set; }

    /// <summary>
    /// The texts of the listing in the languages of the channel manager (NF-31): the language code (<c>en</c>, <c>fr</c>, <c>de</c>...) to its
    /// texts. Left out (or <c>null</c>) the stored translations are kept; an empty object clears them. Who wrote each text is never sent: the
    /// server works it out from what is stored.
    /// </summary>
    [MaxLength(20, ErrorMessage = TooLong)]
    public Dictionary<string, ListingTextsRequest?>? Translations { get; set; }

    public SitePropertyProfileInput ToInput() => new(
        FloorLevel,
        RenovatedYear,
        EnergyClass,
        EnergyIndex,
        AreaKind,
        ChildrenWelcome,
        CheckInTo,
        VideoUrl,
        TourUrl,
        Zone?.ToInput(),
        Near?.ToInput(),
        FloorNote?.ToInput(),
        OutdoorNote?.ToInput(),
        CheckInMethod?.ToInput(),
        RulesNote?.ToInput(),
        Highlights?.Select(h => h.ToInput()).ToList(),
        SafetyNotes?.Select(n => n.ToInput()).ToList(),
        Tags,
        Translations?
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value!.ToInput(), StringComparer.Ordinal));
}

public sealed class SiteTextRequest
{
    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? It { get; set; }

    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? En { get; set; }

    [MaxLength(20, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public Dictionary<string, string?>? Others { get; set; }

    public LocalizedTextInput ToInput() => new(It, En, Others);
}

public sealed class ListingTextsRequest
{
    [MaxLength(4000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Title { get; set; }

    [MaxLength(Casazen.Core.Services.ListingTextLimits.DescriptionMaxLength, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Description { get; set; }

    [MaxLength(4000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Spaces { get; set; }

    [MaxLength(4000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Access { get; set; }

    [MaxLength(4000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Neighborhood { get; set; }

    [MaxLength(4000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Transport { get; set; }

    [MaxLength(4000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Rules { get; set; }

    public ListingTextsInput ToInput() => new(Title, Description, Spaces, Access, Neighborhood, Transport, Rules);
}

public sealed class SiteHighlightRequest
{
    [MaxLength(40, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Icon { get; set; }

    public SiteTextRequest? Title { get; set; }

    public SiteTextRequest? Text { get; set; }

    public SiteHighlightInput ToInput() => new(Icon, Title?.ToInput(), Text?.ToInput());
}

public sealed class PropertyPhotoAltRequest
{
    [MaxLength(Casazen.Core.Services.PropertyPhotoLimits.MaxPhotos, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public List<PropertyPhotoAltItem>? Photos { get; set; }

    public IReadOnlyList<Casazen.Core.Services.PhotoAltInput> ToInput() =>
        [.. (Photos ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Url)).Select(p => new Casazen.Core.Services.PhotoAltInput(p.Url!, p.Alt?.ToInput(), p.RoomId))];
}

public sealed class PropertyPhotoAltItem
{
    [MaxLength(PropertyPhotoRequestLimits.UrlMaxLength, ErrorMessage = "PropertyPhotoUrlTooLong")]
    public string? Url { get; set; }

    public SiteTextRequest? Alt { get; set; }

    public Guid? RoomId { get; set; }
}

public sealed class FloorPlanForm
{
    public IFormFile? Image { get; set; }

    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? NameIt { get; set; }

    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? NameEn { get; set; }

    public int? Level { get; set; }

    public int? AreaSqm { get; set; }

    public Casazen.Core.Services.FloorPlanInput ToInput() => new(new LocalizedTextInput(NameIt, NameEn), Level, AreaSqm);
}

public sealed class FloorPlanRequest
{
    public SiteTextRequest? Name { get; set; }

    public int? Level { get; set; }

    public int? AreaSqm { get; set; }

    public Casazen.Core.Services.FloorPlanInput ToInput() => new(Name?.ToInput(), Level, AreaSqm);
}

public sealed class PoiListRequest
{
    [MaxLength(50, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public List<PoiItem>? Pois { get; set; }

    public IReadOnlyList<Casazen.Core.Services.PoiInput> ToInput() =>
        [.. (Pois ?? []).Select(p => new Casazen.Core.Services.PoiInput(p.Kind, p.Name?.ToInput(), p.Mode, p.Minutes, p.Direction, p.DistanceMeters))];
}

public sealed class PoiItem
{
    public PoiKind Kind { get; set; }

    public SiteTextRequest? Name { get; set; }

    public PoiTravelMode Mode { get; set; }

    public int Minutes { get; set; }

    public CompassPoint Direction { get; set; }

    public int DistanceMeters { get; set; }
}
