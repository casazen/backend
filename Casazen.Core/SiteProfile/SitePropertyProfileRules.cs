using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.SiteProfile;

public static class SitePropertyProfileErrorCodes
{
    public const string TextTooLong = "property_site_profile_text_too_long";

    public const string ItalianRequired = "property_site_profile_italian_required";

    public const string TooManyHighlights = "property_site_profile_too_many_highlights";

    public const string HighlightIconUnknown = "property_site_profile_highlight_icon_unknown";

    public const string TooManySafetyNotes = "property_site_profile_too_many_safety_notes";

    public const string TagUnknown = "property_site_profile_tag_unknown";

    public const string FloorLevelInvalid = "property_site_profile_floor_level_invalid";

    public const string RenovatedYearInvalid = "property_site_profile_renovated_year_invalid";

    public const string EnergyClassInvalid = "property_site_profile_energy_class_invalid";

    public const string EnergyIndexInvalid = "property_site_profile_energy_index_invalid";

    public const string EnergyIndexWithoutClass = "property_site_profile_energy_index_without_class";

    public const string CheckInWindowInvalid = "property_site_profile_check_in_window_invalid";

    public const string VideoUrlInvalid = "property_site_profile_video_url_invalid";

    public const string TourUrlInvalid = "property_site_profile_tour_url_invalid";

    public const string LanguageUnknown = "property_site_profile_language_unknown";
}

public sealed record LocalizedTextInput(string? It, string? En, IReadOnlyDictionary<string, string?>? Others = null);

public sealed record ListingTextsInput(
    string? Title = null,
    string? Description = null,
    string? Spaces = null,
    string? Access = null,
    string? Neighborhood = null,
    string? Transport = null,
    string? Rules = null)
{
    public string? Get(ListingTextField field) => field switch
    {
        ListingTextField.Title => Title,
        ListingTextField.Description => Description,
        ListingTextField.Spaces => Spaces,
        ListingTextField.Access => Access,
        ListingTextField.Neighborhood => Neighborhood,
        ListingTextField.Transport => Transport,
        ListingTextField.Rules => Rules,
        _ => null,
    };
}

public sealed record SiteHighlightInput(string? Icon, LocalizedTextInput? Title, LocalizedTextInput? Text);

/// <summary>
/// What the host sends to save the site profile of a property (DB-11a): the whole profile, as the editor shows it. A part that is
/// left out is cleared, so the form always sends all it shows. The exception is what NF-31 added, so a client that does not know it cannot
/// erase it: <paramref name="Translations"/> left out (<c>null</c>) keeps the stored translations and their origins, and so does a text
/// sent without <see cref="LocalizedTextInput.Others"/>; an empty map clears them.
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
    IReadOnlyList<string>? Tags,
    IReadOnlyDictionary<string, ListingTextsInput>? Translations = null);

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
/// <param name="Translations">The texts of the listing in the languages of the channel manager, by language code (NF-31); Italian only for the transport.</param>
/// <param name="Origins">Who wrote each translated text, by <c>"language.field"</c> (<c>fr.title</c>): a text always has one, a person's where none was recorded.</param>
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
    IReadOnlyDictionary<string, ListingTexts> Translations,
    IReadOnlyDictionary<string, TextOrigin> Origins,
    DateTime UpdatedAt);

public static class SitePropertyProfileRules
{
    /// <summary>
    /// The profile as it will be stored: texts trimmed and cleaned of control characters, the Italian part present wherever there is a
    /// text, lists within their limits, the vocabularies respected, the video and the tour in their canonical embed form.
    /// </summary>
    /// <param name="input">What the host sent.</param>
    /// <param name="checkInFrom">The earliest arrival time already stored (SR-07a), to check the latest against; <c>null</c> = none.</param>
    /// <param name="currentYear">This year, on the clock of Rome: a renovation cannot be later.</param>
    /// <param name="previous">
    /// The profile already stored, <c>null</c> for a property that has none. It gives the origin of the texts of the listing a person saves
    /// (<see cref="ListingTextOrigins.WhenPersonSaves"/>) and what is kept when the input leaves out the translations or the other languages
    /// of a text (NF-31).
    /// </param>
    /// <exception cref="DomainRuleException">422 with one of <see cref="SitePropertyProfileErrorCodes"/> and the <c>field</c> that broke it.</exception>
    public static SitePropertyProfileValues Normalize(
        SitePropertyProfileInput input,
        TimeOnly? checkInFrom,
        int currentYear,
        SitePropertyProfileDocument? previous = null)
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

        var (translations, origins) = NormalizeTranslations(input.Translations, previous);
        var document = new SitePropertyProfileDocument
        {
            Zone = NormalizeText(input.Zone, SitePropertyProfileLimits.ZoneMaxLength, "zone", allowOthers: true, previous: previous?.Zone),
            Near = NormalizeText(input.Near, SitePropertyProfileLimits.NearMaxLength, "near", allowOthers: true, previous: previous?.Near),
            FloorNote = NormalizeText(
                input.FloorNote, SitePropertyProfileLimits.FloorNoteMaxLength, "floorNote", allowOthers: true, previous: previous?.FloorNote),
            OutdoorNote = NormalizeText(
                input.OutdoorNote, SitePropertyProfileLimits.OutdoorNoteMaxLength, "outdoorNote", allowOthers: true, previous: previous?.OutdoorNote),
            CheckInMethod = NormalizeText(
                input.CheckInMethod, SitePropertyProfileLimits.CheckInMethodMaxLength, "checkInMethod", allowOthers: true, previous: previous?.CheckInMethod),
            RulesNote = NormalizeText(
                input.RulesNote, SitePropertyProfileLimits.RulesNoteMaxLength, "rulesNote", multiline: true, allowOthers: true, previous: previous?.RulesNote),
            Highlights = Highlights(input.Highlights),
            SafetyNotes = SafetyNotes(input.SafetyNotes),
            Tags = Tags(input.Tags),
            Translations = translations,
            Origins = origins,
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

    public static IReadOnlyList<string> EffectiveTags(IEnumerable<string> chosen, IEnumerable<string> derived)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(derived);
        var all = new HashSet<string>(chosen.Concat(derived), StringComparer.Ordinal);
        return [.. SitePropertyProfileLimits.Tags.Where(all.Contains)];
    }

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
            document.Translations,
            ListingTextOrigins.Effective(document),
            property.UpdatedAt);
    }

    /// <summary>
    /// The profile after a machine translation wrote <paramref name="translated"/> (NF-31): the document with the translated texts added as
    /// <see cref="TextOrigin.AutoTranslated"/>, except where a person already wrote something. The service call a machine translation will
    /// use is the only way a text gets that origin: nothing a person sends through the host form can carry it.
    /// </summary>
    /// <param name="document">The profile as stored.</param>
    /// <param name="translated">The translated texts, by language code. Italian is not translated into: only the other languages count.</param>
    /// <exception cref="DomainRuleException">422 <see cref="SitePropertyProfileErrorCodes.LanguageUnknown"/> or <see cref="SitePropertyProfileErrorCodes.TextTooLong"/>.</exception>
    public static SitePropertyProfileDocument MergeAutoTranslations(
        SitePropertyProfileDocument document,
        IReadOnlyDictionary<string, ListingTextsInput> translated)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(translated);

        var translations = document.Translations.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var origins = document.Origins.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (rawCode, input) in translated)
        {
            var language = ListingLanguages.Normalize(rawCode)
                ?? throw Error(SitePropertyProfileErrorCodes.LanguageUnknown, "PropertySiteProfileLanguageUnknown", "translations");
            if (language == ListingLanguages.Italian)
                continue;

            var machine = NormalizeListingTexts(input, language);
            var current = translations.GetValueOrDefault(language) ?? new ListingTexts();
            foreach (var field in ListingTextResolver.Fields)
            {
                if (machine.Get(field) is not { } text)
                    continue;

                if (!ListingTextOrigins.MachineMayWrite(current.Get(field), ListingTextOrigins.OriginOf(document, language, field)))
                    continue;

                current = current.With(field, text);
                origins[ListingTextResolver.Key(language, field)] = TextOrigin.AutoTranslated;
            }

            if (!current.IsEmpty)
                translations[language] = current;
        }

        return document with
        {
            Translations = new SortedDictionary<string, ListingTexts>(translations, StringComparer.Ordinal),
            Origins = new SortedDictionary<string, TextOrigin>(origins, StringComparer.Ordinal),
        };
    }

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

        return [.. SitePropertyProfileLimits.Tags.Where(chosen.Contains)];
    }

    public static LocalizedText? NormalizeText(
        LocalizedTextInput? input,
        int maxLength,
        string field,
        bool multiline = false,
        bool allowOthers = false,
        LocalizedText? previous = null)
    {
        var italian = CleanText(input?.It, multiline);
        var english = CleanText(input?.En, multiline);
        var others = allowOthers && input?.Others is { } sent ? NormalizeOthers(sent, maxLength, field, multiline) : null;
        if (italian is null && english is null && others is null)
            return null;

        if (italian is null)
            throw Error(SitePropertyProfileErrorCodes.ItalianRequired, "PropertySiteProfileItalianRequired", field);

        if (italian.Length > maxLength || (english?.Length ?? 0) > maxLength)
            throw Error(SitePropertyProfileErrorCodes.TextTooLong, "PropertySiteProfileTextTooLong", field, maxLength);

        if (allowOthers && input?.Others is null)
            others = previous?.Others;

        return new LocalizedText(italian, english) { Others = others };
    }

    private static IReadOnlyDictionary<string, string>? NormalizeOthers(
        IReadOnlyDictionary<string, string?> sent,
        int maxLength,
        string field,
        bool multiline)
    {
        var others = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawCode, rawText) in sent)
        {
            var language = ListingLanguages.Normalize(rawCode);
            if (language is null or ListingLanguages.Italian or ListingLanguages.English)
                throw Error(SitePropertyProfileErrorCodes.LanguageUnknown, "PropertySiteProfileLanguageUnknown", field);

            var text = CleanText(rawText, multiline);
            if (text is null)
                continue;

            if (text.Length > maxLength)
                throw Error(SitePropertyProfileErrorCodes.TextTooLong, "PropertySiteProfileTextTooLong", field, maxLength);

            others[language] = text;
        }

        return others.Count == 0 ? null : others;
    }

    private static (IReadOnlyDictionary<string, ListingTexts> Translations, IReadOnlyDictionary<string, TextOrigin> Origins) NormalizeTranslations(
        IReadOnlyDictionary<string, ListingTextsInput>? input,
        SitePropertyProfileDocument? previous)
    {
        if (input is null)
            return (previous?.Translations ?? new Dictionary<string, ListingTexts>(), previous?.Origins ?? new Dictionary<string, TextOrigin>());

        var translations = new SortedDictionary<string, ListingTexts>(StringComparer.Ordinal);
        foreach (var (rawCode, texts) in input)
        {
            var language = ListingLanguages.Normalize(rawCode)
                ?? throw Error(SitePropertyProfileErrorCodes.LanguageUnknown, "PropertySiteProfileLanguageUnknown", "translations");

            var merged = translations.GetValueOrDefault(language) ?? new ListingTexts();
            var written = NormalizeListingTexts(texts, language);
            foreach (var field in ListingTextResolver.Fields)
            {
                if (written.Get(field) is { } text)
                    merged = merged.With(field, text);
            }

            if (!merged.IsEmpty)
                translations[language] = merged;
        }

        var origins = new SortedDictionary<string, TextOrigin>(StringComparer.Ordinal);
        foreach (var (language, texts) in translations)
        {
            foreach (var field in ListingTextResolver.Fields)
            {
                if (texts.Get(field) is not { } text)
                    continue;

                var previousText = previous?.Translations.GetValueOrDefault(language)?.Get(field);
                var previousOrigin = previous is null ? TextOrigin.UserWritten : ListingTextOrigins.OriginOf(previous, language, field);
                origins[ListingTextResolver.Key(language, field)] = ListingTextOrigins.WhenPersonSaves(text, previousText, previousOrigin);
            }
        }

        return (translations, origins);
    }

    private static ListingTexts NormalizeListingTexts(ListingTextsInput? texts, string language)
    {
        var result = new ListingTexts();
        if (texts is null)
            return result;

        foreach (var field in ListingTextResolver.Fields)
        {
            if (language == ListingLanguages.Italian && field != ListingTextField.Transport)
                continue;

            var text = CleanText(texts.Get(field), multiline: field != ListingTextField.Title);
            if (text is null)
                continue;

            var maxLength = ListingTextMaxLength(field);
            if (text.Length > maxLength)
            {
                throw Error(
                    SitePropertyProfileErrorCodes.TextTooLong, "PropertySiteProfileTextTooLong", $"translations.{language}.{ListingTextResolver.CodeOf(field)}", maxLength);
            }

            result = result.With(field, text);
        }

        return result;
    }

    public static int ListingTextMaxLength(ListingTextField field) => field switch
    {
        ListingTextField.Title => Casazen.Core.Services.ListingTextLimits.TitleMaxLength,
        ListingTextField.Description => Casazen.Core.Services.ListingTextLimits.DescriptionMaxLength,
        _ => SitePropertyProfileLimits.ListingTextMaxLength,
    };

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
