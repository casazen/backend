using Casazen.Core.DTOs;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;

namespace Casazen.Core.SiteProfile;

public sealed record PublicProfileSource(
    string? ListingTitle,
    AccommodationType? AccommodationType,
    int? Beds,
    int? AreaSqm,
    int? FloorLevel,
    int? RenovatedYear,
    string? EnergyClass,
    decimal? EnergyIndex,
    PropertyAreaKind? AreaKind,
    bool? ChildrenWelcome,
    TimeOnly? CheckInFrom,
    TimeOnly? CheckInTo,
    TimeOnly? CheckOutBy,
    TimeOnly? QuietHoursFrom,
    TimeOnly? QuietHoursTo,
    SmokingPolicy? Smoking,
    PetsPolicy? Pets,
    PartiesPolicy? Parties,
    string? VideoUrl,
    string? TourUrl,
    string? SiteProfileJson,
    IReadOnlyCollection<PropertyAmenity> Amenities);

public static class PublicPropertyProfileBuilder
{
    public static PublicPropertyProfileDto Build(PublicProfileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var document = SitePropertyProfileJson.ParseForPublicPage(source.SiteProfileJson);
        var video = SiteEmbedUrls.NormalizeVideo(source.VideoUrl);
        var tour = SiteEmbedUrls.NormalizeTour(source.TourUrl);
        var derived = SitePropertyProfileRules.DerivedTags(source.Amenities, source.AreaKind, source.Pets);

        return new PublicPropertyProfileDto
        {
            Headline = string.IsNullOrWhiteSpace(source.ListingTitle) ? null : source.ListingTitle,
            AccommodationType = source.AccommodationType,
            Beds = source.Beds,
            AreaSqm = source.AreaSqm,
            FloorLevel = source.FloorLevel,
            FloorNote = document.FloorNote,
            OutdoorNote = document.OutdoorNote,
            RenovatedYear = source.RenovatedYear,
            Energy = string.IsNullOrWhiteSpace(source.EnergyClass) ? null : new PublicPropertyEnergyDto(source.EnergyClass, source.EnergyIndex),
            AreaKind = source.AreaKind,
            Zone = document.Zone,
            Near = document.Near,
            Tags = SitePropertyProfileRules.EffectiveTags(document.Tags, derived),
            Highlights = document.Highlights,
            SafetyNotes = document.SafetyNotes,
            Rules = BuildRules(source, document),
            Video = video is null ? null : new PublicPropertyEmbedDto(video.Url, video.Provider),
            Tour = tour is null ? null : new PublicPropertyEmbedDto(tour.Url, tour.Provider),
        };
    }

    private static PublicPropertyRulesDto? BuildRules(PublicProfileSource source, SitePropertyProfileDocument document)
    {
        var checkInTo = source.CheckInTo is { } latest && source.CheckInFrom is { } earliest && latest <= earliest ? null : source.CheckInTo;
        var checkIn = source.CheckInFrom is null && checkInTo is null && document.CheckInMethod is null
            ? null
            : new PublicCheckInDto(source.CheckInFrom, checkInTo, document.CheckInMethod);

        var quietHours = source.QuietHoursFrom is { } from && source.QuietHoursTo is { } to ? new PublicQuietHoursDto(from, to) : null;

        var rules = new PublicPropertyRulesDto
        {
            CheckIn = checkIn,
            CheckOutBy = source.CheckOutBy,
            QuietHours = quietHours,
            Pets = source.Pets,
            Smoking = source.Smoking,
            Parties = source.Parties,
            ChildrenWelcome = source.ChildrenWelcome,
            Note = document.RulesNote,
        };

        var any = rules.CheckIn is not null || rules.CheckOutBy is not null || rules.QuietHours is not null || rules.Pets is not null
            || rules.Smoking is not null || rules.Parties is not null || rules.ChildrenWelcome is not null || rules.Note is not null;
        return any ? rules : null;
    }
}
