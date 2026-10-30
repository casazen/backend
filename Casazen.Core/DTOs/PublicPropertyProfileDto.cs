using Casazen.Core.Entities.Enums;
using Casazen.Core.SiteProfile;

namespace Casazen.Core.DTOs;

/// <summary>
/// What the host said about the house, as the public property page shows it (DB-11a): specifications, the place, the labels, the highlights,
/// the safety notes, the rules and times, the video and the tour. <b>Only what the host filled in</b>: a member the host never said is
/// <c>null</c> (a list: empty) and the page does not draw its section. Nothing here can hold an address or an exact position. Texts come in
/// Italian and, if the host wrote it, English (<see cref="LocalizedText"/>); the page falls back to Italian.
/// </summary>
public sealed class PublicPropertyProfileDto
{
    /// <summary>The headline of the listing (SR-07a), when the host wrote one different from the name.</summary>
    public string? Headline { get; init; }

    public AccommodationType? AccommodationType { get; init; }

    public int? Beds { get; init; }

    /// <summary>Living area in square metres.</summary>
    public int? AreaSqm { get; init; }

    /// <summary>The floor, 0 = ground floor.</summary>
    public int? FloorLevel { get; init; }

    public LocalizedText? FloorNote { get; init; }

    public LocalizedText? OutdoorNote { get; init; }

    public int? RenovatedYear { get; init; }

    /// <summary>The energy class (and index) the host stated from the APE; <c>null</c> when they stated none. Never filled in by the system.</summary>
    public PublicPropertyEnergyDto? Energy { get; init; }

    /// <summary>What kind of place the house stands in; <c>null</c> = not said.</summary>
    public PropertyAreaKind? AreaKind { get; init; }

    public LocalizedText? Zone { get; init; }

    public LocalizedText? Near { get; init; }

    /// <summary>The labels of the house: the ones the host chose and the ones its data give, in the order of the vocabulary.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<SiteHighlight> Highlights { get; init; } = [];

    public IReadOnlyList<LocalizedText> SafetyNotes { get; init; } = [];

    /// <summary>The rules and times; <c>null</c> when the host said none of them.</summary>
    public PublicPropertyRulesDto? Rules { get; init; }

    /// <summary>The video, embeddable after the visitor's click; <c>null</c> = none.</summary>
    public PublicPropertyEmbedDto? Video { get; init; }

    /// <summary>The 360° tour, embeddable after the visitor's click; <c>null</c> = none.</summary>
    public PublicPropertyEmbedDto? Tour { get; init; }

    /// <summary>The levels of the floor plan, lowest level first; empty = the host uploaded none.</summary>
    public IReadOnlyList<PublicFloorPlanDto> FloorPlans { get; set; } = [];

    /// <summary>The points of interest around the house, in the order the host gave; empty = none. Never an exact position (see <see cref="PublicPoiDto"/>).</summary>
    public IReadOnlyList<PublicPoiDto> Pois { get; set; } = [];
}

/// <summary>The energy class of the APE as the host stated it.</summary>
/// <param name="Class"><c>A4</c> to <c>G</c>.</param>
/// <param name="Index">The global performance index in kWh/m² per year; <c>null</c> = not given.</param>
public sealed record PublicPropertyEnergyDto(string Class, decimal? Index);

/// <summary>The rules and times of the house. Each member is <c>null</c> when the host never said it.</summary>
public sealed class PublicPropertyRulesDto
{
    public PublicCheckInDto? CheckIn { get; init; }

    /// <summary>The time by which guests must leave.</summary>
    public TimeOnly? CheckOutBy { get; init; }

    public PublicQuietHoursDto? QuietHours { get; init; }

    public PetsPolicy? Pets { get; init; }

    public SmokingPolicy? Smoking { get; init; }

    public PartiesPolicy? Parties { get; init; }

    /// <summary><c>true</c> = children are welcome, <c>false</c> = the house is not suited to them.</summary>
    public bool? ChildrenWelcome { get; init; }

    /// <summary>A note the host adds to the rules.</summary>
    public LocalizedText? Note { get; init; }
}

/// <summary>When and how guests arrive. <paramref name="From"/> and <paramref name="To"/> are on the clock of the property.</summary>
public sealed record PublicCheckInDto(TimeOnly? From, TimeOnly? To, LocalizedText? Method);

/// <summary>The quiet hours of the house, on the clock of the property.</summary>
public sealed record PublicQuietHoursDto(TimeOnly From, TimeOnly To);

/// <summary>A video or a tour as an address the page can embed (the canonical embed of one of three providers).</summary>
public sealed record PublicPropertyEmbedDto(string Url, SiteEmbedProvider Provider);

/// <summary>
/// A photo of the gallery with what the page needs to draw it well (DB-11a): the address, the alternative text the host wrote (Italian and,
/// if written, English) and the size in pixels. <c>photoUrls</c> keeps listing the same addresses.
/// </summary>
/// <param name="Url">Absolute public address of the photo.</param>
/// <param name="Alt">The alternative text; <c>null</c> = the host wrote none (the page then describes the photo by the name of the house).</param>
/// <param name="Width">Width in pixels; <c>null</c> = not known.</param>
/// <param name="Height">Height in pixels; <c>null</c> = not known.</param>
public sealed record PublicPropertyPhotoDto(string Url, LocalizedText? Alt, int? Width, int? Height);

/// <summary>One level of the floor plan of a house.</summary>
/// <param name="Id">Identifier of the level, for the host editor.</param>
/// <param name="Level">The floor, 0 = ground floor.</param>
/// <param name="Name">What the guest reads: «Piano terra», «Mansarda».</param>
/// <param name="ImageUrl">Absolute public address of the image (PNG or JPEG).</param>
/// <param name="Width">Width of the image in pixels; <c>null</c> = not known.</param>
/// <param name="Height">Height of the image in pixels; <c>null</c> = not known.</param>
/// <param name="AreaSqm">The living area of the level in square metres; <c>null</c> = not said.</param>
public sealed record PublicFloorPlanDto(Guid Id, int Level, LocalizedText Name, string ImageUrl, int? Width, int? Height, int? AreaSqm);

/// <summary>
/// A point of interest near the house. <b>Never a position</b>: the host states the minutes, the distance and the side of the compass, and the
/// server keeps the side as one of eight points and the distance rounded and not closer than 200 m (<see cref="Casazen.Core.SiteProfile.PoiDistance"/>),
/// so a place that is known does not give the door of the house away.
/// </summary>
/// <param name="Kind">Beach, food, shop, transport, sight, health or parking.</param>
/// <param name="Name">What the guest reads, in Italian and, if written, English.</param>
/// <param name="Mode">On foot or by car: how the minutes are travelled.</param>
/// <param name="Minutes">Minutes to get there.</param>
/// <param name="Direction">Side of the compass the place lies on, seen from the house.</param>
/// <param name="DistanceMeters">Distance in metres, rounded.</param>
public sealed record PublicPoiDto(PoiKind Kind, LocalizedText Name, PoiTravelMode Mode, int Minutes, CompassPoint Direction, int DistanceMeters);
