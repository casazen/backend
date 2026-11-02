using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;
using Microsoft.AspNetCore.Http;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of <c>PUT /api/properties/{id}/site-profile</c> (DB-11a): the whole site profile of a property, as the editor shows it. The save
/// <b>replaces</b> the profile: whatever the body leaves out (or sends as <c>null</c> or empty) is cleared, so the form sends everything it
/// shows. The limits that depend on the field (a zone is shorter than a note on the rules) are checked by the server with a 422 that
/// names the <c>field</c>; the caps here only keep an absurd body out.
/// </summary>
public sealed class SitePropertyProfileRequest
{
    private const string TooLong = "PropertySiteProfilePayloadTooLong";

    /// <summary>The floor, <c>-3</c> to <c>60</c>, <c>0</c> = ground floor.</summary>
    public int? FloorLevel { get; set; }

    /// <summary>The year of the last renovation, 1800 to this year.</summary>
    public int? RenovatedYear { get; set; }

    /// <summary>The energy class of the APE, <c>A4</c> to <c>G</c>. Never guessed by the system: left out, it is simply not shown.</summary>
    [MaxLength(16, ErrorMessage = TooLong)]
    public string? EnergyClass { get; set; }

    /// <summary>The global energy performance index in kWh/m² per year; only together with <see cref="EnergyClass"/>.</summary>
    public decimal? EnergyIndex { get; set; }

    /// <summary>The kind of place: <c>Country</c>, <c>Coast</c> or <c>Historic</c>.</summary>
    public PropertyAreaKind? AreaKind { get; set; }

    /// <summary><c>true</c> = children are welcome; <c>false</c> = the house is not suited to them; <c>null</c> = not said.</summary>
    public bool? ChildrenWelcome { get; set; }

    /// <summary>The latest arrival time (<c>HH:mm</c>), after the earliest one of the house rules when that is set.</summary>
    public TimeOnly? CheckInTo { get; set; }

    /// <summary>A YouTube or Vimeo address (any form the provider gives); stored as the canonical embed address.</summary>
    [MaxLength(2000, ErrorMessage = TooLong)]
    public string? VideoUrl { get; set; }

    /// <summary>A Matterport address; stored as the canonical embed address.</summary>
    [MaxLength(2000, ErrorMessage = TooLong)]
    public string? TourUrl { get; set; }

    public SiteTextRequest? Zone { get; set; }

    public SiteTextRequest? Near { get; set; }

    public SiteTextRequest? FloorNote { get; set; }

    public SiteTextRequest? OutdoorNote { get; set; }

    public SiteTextRequest? CheckInMethod { get; set; }

    public SiteTextRequest? RulesNote { get; set; }

    /// <summary>Up to three highlights.</summary>
    [MaxLength(20, ErrorMessage = TooLong)]
    public List<SiteHighlightRequest>? Highlights { get; set; }

    /// <summary>Up to ten safety notes.</summary>
    [MaxLength(50, ErrorMessage = TooLong)]
    public List<SiteTextRequest>? SafetyNotes { get; set; }

    /// <summary>Ids of the labels of the site (<c>mare</c>, <c>centro</c>, <c>piscina</c>, <c>pet</c>, <c>campagna</c>, <c>terrazza</c>, <c>vista-mare</c>).</summary>
    [MaxLength(50, ErrorMessage = TooLong)]
    public List<string>? Tags { get; set; }

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
        Tags);
}

/// <summary>A text in Italian (always, wherever there is a text) and, if the host wants, in English.</summary>
public sealed class SiteTextRequest
{
    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? It { get; set; }

    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? En { get; set; }

    public LocalizedTextInput ToInput() => new(It, En);
}

/// <summary>One highlight: an icon of the fixed list, a short title and an optional line.</summary>
public sealed class SiteHighlightRequest
{
    [MaxLength(40, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? Icon { get; set; }

    public SiteTextRequest? Title { get; set; }

    public SiteTextRequest? Text { get; set; }

    public SiteHighlightInput ToInput() => new(Icon, Title?.ToInput(), Text?.ToInput());
}

/// <summary>Body of <c>PUT /api/properties/{id}/site-profile/photos</c>: the photos that have an alternative text; the others lose theirs.</summary>
public sealed class PropertyPhotoAltRequest
{
    /// <summary>At most as many as a gallery can hold.</summary>
    [MaxLength(Casazen.Core.Services.PropertyPhotoLimits.MaxPhotos, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public List<PropertyPhotoAltItem>? Photos { get; set; }

    public IReadOnlyList<Casazen.Core.Services.PhotoAltInput> ToInput() =>
        [.. (Photos ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Url)).Select(p => new Casazen.Core.Services.PhotoAltInput(p.Url!, p.Alt?.ToInput()))];
}

/// <summary>The alternative text of one photo.</summary>
public sealed class PropertyPhotoAltItem
{
    /// <summary>The address of the photo, as the gallery lists it.</summary>
    [MaxLength(PropertyPhotoRequestLimits.UrlMaxLength, ErrorMessage = "PropertyPhotoUrlTooLong")]
    public string? Url { get; set; }

    /// <summary>The text in Italian and, if the host wants, English; blank = none.</summary>
    public SiteTextRequest? Alt { get; set; }
}

/// <summary>Form of <c>POST /api/properties/{id}/site-profile/floor-plans</c> (multipart): the image of a level and what the host says of it.</summary>
public sealed class FloorPlanForm
{
    /// <summary>The image: PNG or JPEG, 5 MB at most, checked on its content.</summary>
    public IFormFile? Image { get; set; }

    /// <summary>The name in Italian (required), e.g. «Piano terra».</summary>
    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? NameIt { get; set; }

    /// <summary>The name in English (optional).</summary>
    [MaxLength(2000, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public string? NameEn { get; set; }

    /// <summary>The floor, -3 to 60, 0 = ground floor (left out = 0).</summary>
    public int? Level { get; set; }

    /// <summary>The living area of the level in square metres (optional).</summary>
    public int? AreaSqm { get; set; }

    public Casazen.Core.Services.FloorPlanInput ToInput() => new(new LocalizedTextInput(NameIt, NameEn), Level, AreaSqm);
}

/// <summary>Body of <c>PUT /api/properties/{id}/site-profile/floor-plans/{planId}</c>: the name, the floor and the area of a level (the image stays).</summary>
public sealed class FloorPlanRequest
{
    public SiteTextRequest? Name { get; set; }

    public int? Level { get; set; }

    public int? AreaSqm { get; set; }

    public Casazen.Core.Services.FloorPlanInput ToInput() => new(Name?.ToInput(), Level, AreaSqm);
}

/// <summary>Body of <c>PUT /api/properties/{id}/site-profile/pois</c>: the whole list of points of interest, which replaces the stored one.</summary>
public sealed class PoiListRequest
{
    /// <summary>At most twelve.</summary>
    [MaxLength(50, ErrorMessage = "PropertySiteProfilePayloadTooLong")]
    public List<PoiItem>? Pois { get; set; }

    public IReadOnlyList<Casazen.Core.Services.PoiInput> ToInput() =>
        [.. (Pois ?? []).Select(p => new Casazen.Core.Services.PoiInput(p.Kind, p.Name?.ToInput(), p.Mode, p.Minutes, p.Direction, p.DistanceMeters))];
}

/// <summary>One point of interest: what it is, its name, how far it is in minutes and metres and on which side of the compass.</summary>
public sealed class PoiItem
{
    /// <summary><c>Beach</c>, <c>Food</c>, <c>Shop</c>, <c>Transport</c>, <c>Sight</c>, <c>Health</c> or <c>Parking</c>.</summary>
    public PoiKind Kind { get; set; }

    /// <summary>The name in Italian (required) and English (optional).</summary>
    public SiteTextRequest? Name { get; set; }

    /// <summary><c>Walk</c> or <c>Car</c>: how the minutes are travelled.</summary>
    public PoiTravelMode Mode { get; set; }

    /// <summary>Minutes to get there, 1 to 600.</summary>
    public int Minutes { get; set; }

    /// <summary>The side of the compass seen from the house: <c>N</c>, <c>NE</c>, <c>E</c>, <c>SE</c>, <c>S</c>, <c>SW</c>, <c>W</c> or <c>NW</c>.</summary>
    public CompassPoint Direction { get; set; }

    /// <summary>The distance in metres; stored rounded and never closer than 200 m (it is not a position).</summary>
    public int DistanceMeters { get; set; }
}
