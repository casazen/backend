using System.Text.Json.Serialization;
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
    public string? Headline { get; init; }

    public AccommodationType? AccommodationType { get; init; }

    public int? Beds { get; init; }

    public int? AreaSqm { get; init; }

    public int? FloorLevel { get; init; }

    public LocalizedText? FloorNote { get; init; }

    public LocalizedText? OutdoorNote { get; init; }

    public int? RenovatedYear { get; init; }

    public PublicPropertyEnergyDto? Energy { get; init; }

    public PropertyAreaKind? AreaKind { get; init; }

    public LocalizedText? Zone { get; init; }

    public LocalizedText? Near { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<SiteHighlight> Highlights { get; init; } = [];

    public IReadOnlyList<LocalizedText> SafetyNotes { get; init; } = [];

    public PublicPropertyRulesDto? Rules { get; init; }

    public PublicPropertyEmbedDto? Video { get; init; }

    public PublicPropertyEmbedDto? Tour { get; init; }

    public IReadOnlyList<PublicFloorPlanDto> FloorPlans { get; set; } = [];

    public IReadOnlyList<PublicPoiDto> Pois { get; set; } = [];
}

public sealed record PublicPropertyEnergyDto(string Class, decimal? Index);

public sealed class PublicPropertyRulesDto
{
    public PublicCheckInDto? CheckIn { get; init; }

    public TimeOnly? CheckOutBy { get; init; }

    public PublicQuietHoursDto? QuietHours { get; init; }

    public PetsPolicy? Pets { get; init; }

    public SmokingPolicy? Smoking { get; init; }

    public PartiesPolicy? Parties { get; init; }

    public bool? ChildrenWelcome { get; init; }

    public LocalizedText? Note { get; init; }
}

public sealed record PublicCheckInDto(TimeOnly? From, TimeOnly? To, LocalizedText? Method);

public sealed record PublicQuietHoursDto(TimeOnly From, TimeOnly To);

public sealed record PublicPropertyEmbedDto(string Url, SiteEmbedProvider Provider);

/// <summary>
/// A photo of the gallery with what the page needs to draw it well (DB-11a): the address, the alternative text the host wrote (Italian and,
/// if written, English) and the size in pixels. <c>photoUrls</c> keeps listing the same addresses.
/// </summary>
/// <param name="Url">Absolute public address of the photo.</param>
/// <param name="Alt">The alternative text; <c>null</c> = the host wrote none (the page then describes the photo by the name of the house).</param>
/// <param name="Width">Width in pixels; <c>null</c> = not known.</param>
/// <param name="Height">Height in pixels; <c>null</c> = not known.</param>
/// <param name="RoomId">
/// The room of the unit the photo shows (NF-32, NF-34): given to the host editor only, never to a public page; left out of the answer when <c>null</c>.
/// </param>
public sealed record PublicPropertyPhotoDto(
    string Url,
    LocalizedText? Alt,
    int? Width,
    int? Height,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RoomId = null);

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
