using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.SiteProfile;

/// <summary>Stable <c>code</c> values of the site profile (422): part of the API contract, never rename one. Each carries <c>field</c> in the problem.</summary>
public static class SitePropertyProfileErrorCodes
{
    /// <summary>A text is longer than the field allows.</summary>
    public const string TextTooLong = "property_site_profile_text_too_long";

    /// <summary>A text has an English part and no Italian one: Italian is always written.</summary>
    public const string ItalianRequired = "property_site_profile_italian_required";

    public const string TooManyHighlights = "property_site_profile_too_many_highlights";

    public const string HighlightIconUnknown = "property_site_profile_highlight_icon_unknown";

    public const string TooManySafetyNotes = "property_site_profile_too_many_safety_notes";

    public const string TagUnknown = "property_site_profile_tag_unknown";

    public const string FloorLevelInvalid = "property_site_profile_floor_level_invalid";

    public const string RenovatedYearInvalid = "property_site_profile_renovated_year_invalid";

    public const string EnergyClassInvalid = "property_site_profile_energy_class_invalid";

    public const string EnergyIndexInvalid = "property_site_profile_energy_index_invalid";

    /// <summary>An energy index without the class it belongs to.</summary>
    public const string EnergyIndexWithoutClass = "property_site_profile_energy_index_without_class";

    /// <summary>The latest arrival time is not after the earliest one.</summary>
    public const string CheckInWindowInvalid = "property_site_profile_check_in_window_invalid";

    public const string VideoUrlInvalid = "property_site_profile_video_url_invalid";

    public const string TourUrlInvalid = "property_site_profile_tour_url_invalid";
}

/// <summary>A text as the host form sends it: both parts optional until the rules have looked at them.</summary>
public sealed record LocalizedTextInput(string? It, string? En);

/// <summary>A highlight as the host form sends it.</summary>
public sealed record SiteHighlightInput(string? Icon, LocalizedTextInput? Title, LocalizedTextInput? Text);

/// <summary>
/// What the host sends to save the site profile of a property (DB-11a): the whole profile, as the editor shows it. A part that is
/// left out is cleared, so the form always sends all it shows.
/// </summary>
public sealed record SitePropertyProfileInput(
    int? FloorLevel,
    int? RenovatedYear,
    string? EnergyClass,
    decimal? EnergyIndex,
    PropertyAreaKind? AreaKind,
    bool? ChildrenWelcome,
    TimeOnly? CheckInTo,
    string? VideoUrl,
    string? TourUrl,
    LocalizedTextInput? Zone,
    LocalizedTextInput? Near,
    LocalizedTextInput? FloorNote,
    LocalizedTextInput? OutdoorNote,
    LocalizedTextInput? CheckInMethod,
    LocalizedTextInput? RulesNote,
    IReadOnlyList<SiteHighlightInput>? Highlights,
    IReadOnlyList<LocalizedTextInput>? SafetyNotes,
    IReadOnlyList<string>? Tags);

/// <summary>The profile after the rules: what is stored in the columns of the property.</summary>
public sealed record SitePropertyProfileValues(
    int? FloorLevel,
    int? RenovatedYear,
    string? EnergyClass,
    decimal? EnergyIndex,
    PropertyAreaKind? AreaKind,
    bool? ChildrenWelcome,
    TimeOnly? CheckInTo,
    string? VideoUrl,
    string? TourUrl,
    SitePropertyProfileDocument Document);

/// <summary>
/// The site profile of a property as the host editor reads it (and as the save answers). Mirrors what is sent, plus what the system adds
/// by itself, which the host can see but not remove (<see cref="DerivedTags"/>).
/// </summary>
/// <param name="VideoUrl">The canonical embed address of the video; <c>null</c> = none.</param>
/// <param name="VideoProvider"><see cref="SiteEmbedProvider"/> of <paramref name="VideoUrl"/>.</param>
/// <param name="TourUrl">The canonical embed address of the 360° tour; <c>null</c> = none.</param>
/// <param name="Tags">The labels the host chose.</param>
/// <param name="DerivedTags">The labels the data of the house gives it (a pool, a terrace, a place on the coast...): shown on the site whatever the host chose.</param>
public sealed record SitePropertyProfileView(
    Guid PropertyId,
    int? FloorLevel,
    int? RenovatedYear,
    string? EnergyClass,
    decimal? EnergyIndex,
    PropertyAreaKind? AreaKind,
    bool? ChildrenWelcome,
    TimeOnly? CheckInTo,
    string? VideoUrl,
    SiteEmbedProvider? VideoProvider,
    string? TourUrl,
    LocalizedText? Zone,
    LocalizedText? Near,
    LocalizedText? FloorNote,
    LocalizedText? OutdoorNote,
    LocalizedText? CheckInMethod,
    LocalizedText? RulesNote,
    IReadOnlyList<SiteHighlight> Highlights,
    IReadOnlyList<LocalizedText> SafetyNotes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> DerivedTags,
    DateTime UpdatedAt);

/// <summary>
/// The rules of the site profile (DB-11a): what is accepted, how it is stored, and what the system adds. One place for the host form, the
/// service, the public page and the tests. Everything here is optional and nothing is read to decide a booking, a price or a refund.
/// </summary>
public static class SitePropertyProfileRules
{
    /// <summary>
    /// The profile as it will be stored: texts trimmed and cleaned of control characters, the Italian part present wherever there is a
    /// text, lists within their limits, the vocabularies respected, the video and the tour in their canonical embed form.
    /// </summary>
    /// <param name="input">What the host sent.</param>
    /// <param name="checkInFrom">The earliest arrival time already stored (SR-07a), to check the latest against; <c>null</c> = none.</param>
    /// <param name="currentYear">This year, on the clock of Rome: a renovation cannot be later.</param>
    /// <exception cref="DomainRuleException">422 with one of <see cref="SitePropertyProfileErrorCodes"/> and the <c>field</c> that broke it.</exception>
    public static SitePropertyProfileValues Normalize(SitePropertyProfileInput input, TimeOnly? checkInFrom, int currentYear)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.FloorLevel is { } floor && floor is < SitePropertyProfileLimits.MinFloorLevel or > SitePropertyProfileLimits.MaxFloorLevel)
        {
            throw Error(
                SitePropertyProfileErrorCodes.FloorLevelInvalid, "PropertySiteProfileFloorLevelInvalid", "floorLevel",
                SitePropertyProfileLimits.MinFloorLevel, SitePropertyProfileLimits.MaxFloorLevel);
        }

        if (input.RenovatedYear is { } year && (year < SitePropertyProfileLimits.MinRenovatedYear || year > currentYear))
        {
            throw Error(
                SitePropertyProfileErrorCodes.RenovatedYearInvalid, "PropertySiteProfileRenovatedYearInvalid", "renovatedYear",
                SitePropertyProfileLimits.MinRenovatedYear, currentYear);
        }

        var energyClass = NormalizeEnergyClass(input.EnergyClass);
        var energyIndex = NormalizeEnergyIndex(input.EnergyIndex, energyClass);

        var checkInTo = PropertyHouseRules.ToWholeMinutes(input.CheckInTo);
        if (checkInTo is { } latest && checkInFrom is { } earliest && latest <= earliest)
            throw Error(SitePropertyProfileErrorCodes.CheckInWindowInvalid, "PropertySiteProfileCheckInWindowInvalid", "checkInTo");

        var video = string.IsNullOrWhiteSpace(input.VideoUrl) ? null : SiteEmbedUrls.NormalizeVideo(input.VideoUrl)
            ?? throw Error(SitePropertyProfileErrorCodes.VideoUrlInvalid, "PropertySiteProfileVideoUrlInvalid", "videoUrl");
        var tour = string.IsNullOrWhiteSpace(input.TourUrl) ? null : SiteEmbedUrls.NormalizeTour(input.TourUrl)
            ?? throw Error(SitePropertyProfileErrorCodes.TourUrlInvalid, "PropertySiteProfileTourUrlInvalid", "tourUrl");

        var document = new SitePropertyProfileDocument
        {
            Zone = NormalizeText(input.Zone, SitePropertyProfileLimits.ZoneMaxLength, "zone"),
            Near = NormalizeText(input.Near, SitePropertyProfileLimits.NearMaxLength, "near"),
            FloorNote = NormalizeText(input.FloorNote, SitePropertyProfileLimits.FloorNoteMaxLength, "floorNote"),
            OutdoorNote = NormalizeText(input.OutdoorNote, SitePropertyProfileLimits.OutdoorNoteMaxLength, "outdoorNote"),
            CheckInMethod = NormalizeText(input.CheckInMethod, SitePropertyProfileLimits.CheckInMethodMaxLength, "checkInMethod"),
            RulesNote = NormalizeText(input.RulesNote, SitePropertyProfileLimits.RulesNoteMaxLength, "rulesNote", multiline: true),
            Highlights = Highlights(input.Highlights),
            SafetyNotes = SafetyNotes(input.SafetyNotes),
            Tags = Tags(input.Tags),
        };

        return new SitePropertyProfileValues(
            input.FloorLevel,
            input.RenovatedYear,
            energyClass,
            energyIndex,
            input.AreaKind,
            input.ChildrenWelcome,
            checkInTo,
            video?.Url,
            tour?.Url,
            document);
    }

    /// <summary>
    /// The labels of the site a house has from its own data, in the order of <see cref="SitePropertyProfileLimits.Tags"/>: a pool gives
    /// «piscina», a terrace «terrazza», a place that welcomes pets «pet», the coast (or a beach) «mare», a historic centre «centro»,
    /// the countryside «campagna». «Vista mare» comes only from the host. The host cannot remove one of these, only change the data.
    /// </summary>
    public static IReadOnlyList<string> DerivedTags(IEnumerable<PropertyAmenity> amenities, PropertyAreaKind? areaKind, PetsPolicy? pets)
    {
        ArgumentNullException.ThrowIfNull(amenities);
        var has = new HashSet<PropertyAmenity>(amenities);
        var derived = new HashSet<string>(StringComparer.Ordinal);

        if (has.Contains(PropertyAmenity.Pool))
            derived.Add("piscina");
        if (has.Contains(PropertyAmenity.Terrace))
            derived.Add("terrazza");
        if (has.Contains(PropertyAmenity.PetFriendly) || pets == PetsPolicy.Yes)
            derived.Add("pet");
        if (has.Contains(PropertyAmenity.BeachAccess) || areaKind == PropertyAreaKind.Coast)
            derived.Add("mare");
        if (areaKind == PropertyAreaKind.Historic)
            derived.Add("centro");
        if (areaKind == PropertyAreaKind.Country)
            derived.Add("campagna");

        return [.. SitePropertyProfileLimits.Tags.Where(derived.Contains)];
    }

    /// <summary>The labels a house shows: the ones the host chose and the ones its data give, each once, in the order of the vocabulary.</summary>
    public static IReadOnlyList<string> EffectiveTags(IEnumerable<string> chosen, IEnumerable<string> derived)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(derived);
        var all = new HashSet<string>(chosen.Concat(derived), StringComparer.Ordinal);
        return [.. SitePropertyProfileLimits.Tags.Where(all.Contains)];
    }

    /// <summary>The profile of a property as the host editor reads it.</summary>
    public static SitePropertyProfileView ToView(Property property)
    {
        ArgumentNullException.ThrowIfNull(property);

        var document = SitePropertyProfileJson.Parse(property.SiteProfileJson);
        var video = SiteEmbedUrls.NormalizeVideo(property.VideoUrl);
        var tour = SiteEmbedUrls.NormalizeTour(property.TourUrl);
        return new SitePropertyProfileView(
            property.Id,
            property.FloorLevel,
            property.RenovatedYear,
            property.EnergyClass,
            property.EnergyIndex,
            property.AreaKind,
            property.ChildrenWelcome,
            property.CheckInTo,
            video?.Url,
            video?.Provider,
            tour?.Url,
            document.Zone,
            document.Near,
            document.FloorNote,
            document.OutdoorNote,
            document.CheckInMethod,
            document.RulesNote,
            document.Highlights,
            document.SafetyNotes,
            [.. SitePropertyProfileLimits.Tags.Where(document.Tags.Contains)],
            DerivedTags(property.Amenities, property.AreaKind, property.PetsPolicy),
            property.UpdatedAt);
    }

    /// <summary>Writes the normalized profile into the columns of the property; the caller saves.</summary>
    public static void Apply(Property property, SitePropertyProfileValues values)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);

        property.FloorLevel = values.FloorLevel;
        property.RenovatedYear = values.RenovatedYear;
        property.EnergyClass = values.EnergyClass;
        property.EnergyIndex = values.EnergyIndex;
        property.AreaKind = values.AreaKind;
        property.ChildrenWelcome = values.ChildrenWelcome;
        property.CheckInTo = values.CheckInTo;
        property.VideoUrl = values.VideoUrl;
        property.TourUrl = values.TourUrl;
        property.SiteProfileJson = SitePropertyProfileJson.Serialize(values.Document);
    }

    private static string? NormalizeEnergyClass(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToUpperInvariant();
        return SitePropertyProfileLimits.EnergyClasses.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : throw Error(SitePropertyProfileErrorCodes.EnergyClassInvalid, "PropertySiteProfileEnergyClassInvalid", "energyClass");
    }

    private static decimal? NormalizeEnergyIndex(decimal? value, string? energyClass)
    {
        if (value is not { } index)
            return null;

        if (index < 0m || index > SitePropertyProfileLimits.MaxEnergyIndex)
        {
            throw Error(
                SitePropertyProfileErrorCodes.EnergyIndexInvalid, "PropertySiteProfileEnergyIndexInvalid", "energyIndex",
                SitePropertyProfileLimits.MaxEnergyIndex);
        }

        // An index states the class's performance: alone it says nothing a guest can compare, and it would hide that the class is missing.
        if (energyClass is null)
            throw Error(SitePropertyProfileErrorCodes.EnergyIndexWithoutClass, "PropertySiteProfileEnergyIndexWithoutClass", "energyIndex");

        return Math.Round(index, SitePropertyProfileLimits.EnergyIndexScale, MidpointRounding.AwayFromZero);
    }

    private static List<SiteHighlight> Highlights(IReadOnlyList<SiteHighlightInput>? input)
    {
        var result = new List<SiteHighlight>();
        foreach (var item in input ?? [])
        {
            var title = NormalizeText(item?.Title, SitePropertyProfileLimits.HighlightTitleMaxLength, "highlights");
            var text = NormalizeText(item?.Text, SitePropertyProfileLimits.HighlightTextMaxLength, "highlights");
            var icon = item?.Icon?.Trim().ToLowerInvariant();

            // A row of the form the host left empty is not a highlight.
            if (title is null && text is null && string.IsNullOrEmpty(icon))
                continue;

            if (title is null)
                throw Error(SitePropertyProfileErrorCodes.ItalianRequired, "PropertySiteProfileItalianRequired", "highlights");

            if (icon is null || !SitePropertyProfileLimits.HighlightIcons.Contains(icon, StringComparer.Ordinal))
                throw Error(SitePropertyProfileErrorCodes.HighlightIconUnknown, "PropertySiteProfileHighlightIconUnknown", "highlights");

            result.Add(new SiteHighlight(icon, title, text));
        }

        if (result.Count > SitePropertyProfileLimits.MaxHighlights)
        {
            throw Error(
                SitePropertyProfileErrorCodes.TooManyHighlights, "PropertySiteProfileTooManyHighlights", "highlights",
                SitePropertyProfileLimits.MaxHighlights);
        }

        return result;
    }

    private static List<LocalizedText> SafetyNotes(IReadOnlyList<LocalizedTextInput>? input)
    {
        var result = new List<LocalizedText>();
        foreach (var item in input ?? [])
        {
            if (NormalizeText(item, SitePropertyProfileLimits.SafetyNoteMaxLength, "safetyNotes") is { } note)
                result.Add(note);
        }

        if (result.Count > SitePropertyProfileLimits.MaxSafetyNotes)
        {
            throw Error(
                SitePropertyProfileErrorCodes.TooManySafetyNotes, "PropertySiteProfileTooManySafetyNotes", "safetyNotes",
                SitePropertyProfileLimits.MaxSafetyNotes);
        }

        return result;
    }

    private static List<string> Tags(IReadOnlyList<string>? input)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in input ?? [])
        {
            var tag = raw?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(tag))
                continue;

            if (!SitePropertyProfileLimits.Tags.Contains(tag, StringComparer.Ordinal))
                throw Error(SitePropertyProfileErrorCodes.TagUnknown, "PropertySiteProfileTagUnknown", "tags");

            chosen.Add(tag);
        }

        // The order of the vocabulary, so the same choice is always stored the same way.
        return [.. SitePropertyProfileLimits.Tags.Where(chosen.Contains)];
    }

    /// <summary>
    /// A text as stored (<see cref="CleanText"/>), Italian always present, within <paramref name="maxLength"/>; <c>null</c> when the host wrote nothing.
    /// </summary>
    /// <exception cref="DomainRuleException">422 <see cref="SitePropertyProfileErrorCodes.ItalianRequired"/> or <see cref="SitePropertyProfileErrorCodes.TextTooLong"/> with the <paramref name="field"/>.</exception>
    public static LocalizedText? NormalizeText(LocalizedTextInput? input, int maxLength, string field, bool multiline = false)
    {
        var italian = CleanText(input?.It, multiline);
        var english = CleanText(input?.En, multiline);
        if (italian is null && english is null)
            return null;

        if (italian is null)
            throw Error(SitePropertyProfileErrorCodes.ItalianRequired, "PropertySiteProfileItalianRequired", field);

        if (italian.Length > maxLength || (english?.Length ?? 0) > maxLength)
            throw Error(SitePropertyProfileErrorCodes.TextTooLong, "PropertySiteProfileTextTooLong", field, maxLength);

        return new LocalizedText(italian, english);
    }

    /// <summary>
    /// A text as stored: no control characters, one space where there were several (and, in a one-line text, where there were line
    /// breaks), trimmed; <c>null</c> when nothing is left. A multi-line text keeps its line breaks, at most one blank line in a row.
    /// </summary>
    public static string? CleanText(string? value, bool multiline = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        var pendingBreaks = 0;
        foreach (var c in value.AsSpan())
        {
            if (c is '\n' or '\r')
            {
                if (c == '\n')
                    pendingBreaks++;

                pendingSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                // A tab or a no-break space is a space; any other control character is dropped.
                if (char.IsWhiteSpace(c))
                    pendingSpace = true;

                continue;
            }

            if (builder.Length > 0)
            {
                if (multiline && pendingBreaks > 0)
                    builder.Append('\n', Math.Min(pendingBreaks, 2));
                else if (pendingSpace || pendingBreaks > 0)
                    builder.Append(' ');
            }

            pendingSpace = false;
            pendingBreaks = 0;
            builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static DomainRuleException Error(string code, string messageKey, string field, params object[] args) =>
        new(code, messageKey, args) { Extensions = new Dictionary<string, object?> { ["field"] = field } };
}
