using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.SiteProfile;

namespace Casazen.Core.Assistant;

/// <summary>
/// One fact of the knowledge base of a property that was read from its listing (E05/F05/US01/T02, NF-66): the <see cref="Topic"/> it answers, the
/// <see cref="Text"/> in Italian or English, and the <see cref="SourceField"/> it came from (one of <see cref="ListingFactSources"/>), so the screen can say «from your
/// listing» and send the host to the right field. A fact is built on every read and never stored.
/// </summary>
public sealed record ListingFact(KnowledgeTopic Topic, string Text, string SourceField);

/// <summary>The names of the fields of the listing a <see cref="ListingFact"/> can come from: stable text, the camel-case name of the field of the property.</summary>
public static class ListingFactSources
{
    public const string CheckInWindow = "checkInWindow";
    public const string CheckInMethod = "checkInMethod";
    public const string CheckOutBy = "checkOutBy";
    public const string Amenities = "amenities";
    public const string HouseRules = "houseRules";
    public const string RulesNote = "rulesNote";
    public const string SmokingPolicy = "smokingPolicy";
    public const string PartiesPolicy = "partiesPolicy";
    public const string ChildrenPolicy = "childrenPolicy";
    public const string QuietHours = "quietHours";
    public const string PetsPolicy = "petsPolicy";
    public const string Address = "address";
    public const string Zone = "zone";
    public const string Near = "near";
    public const string FloorNote = "floorNote";
    public const string OutdoorNote = "outdoorNote";
}

/// <summary>
/// Builds the facts of a property from data the host already entered (E05/F05/US01/T02, NF-66, AC-NF66-1), in Italian or English, pure: no database, no clock, and
/// <b>nothing is stored</b>, so a fact never goes stale when the host changes the listing. What it reads: the check-in window and method, the check-out time, whether
/// there is Wi-Fi, the kinds of parking, the house rules (the text, the note, smoking, parties, children), pets, the quiet hours, the labels of the amenities, and the
/// directions (address, zone, what is near, the notes about the floor and the outdoor spaces).
/// </summary>
/// <remarks>
/// <para><b>What it never reads.</b> The Wi-Fi password (the listing has none: only the presence of the amenity is read, a password can only be a custom entry of the
/// knowledge base) and the access code of the property (released only by <c>IAccessCodeService</c> after verification: it is not an input of this class at all).</para>
/// <para><b>It never asserts an absence.</b> A property with no Wi-Fi ticked, no parking ticked or no answer about pets gets no fact about it, not «there is no
/// Wi-Fi»: the listing says what the host chose to say, and a topic with no fact is reported as a gap for the host to fill in (<see cref="TopicsWithoutData"/>).</para>
/// <para><b>Language.</b> The frame of each sentence is Italian or English (<see cref="ResolveLanguage"/>); the texts the host wrote are used as written, in the language
/// asked when the host wrote it too (<see cref="LocalizedText"/>), in Italian otherwise.</para>
/// </remarks>
public static class ListingKnowledgeBuilder
{
    public const string Italian = "it";

    public const string English = "en";

    /// <summary>The topics a host can be asked about when the knowledge base has nothing on them: every topic but the open one of the FAQs.</summary>
    public static IReadOnlyList<KnowledgeTopic> Topics { get; } =
        [.. Enum.GetValues<KnowledgeTopic>().Where(topic => topic != KnowledgeTopic.Faq)];

    /// <summary><c>en</c> for an English request, Italian (the language of the product) for everything else, including no language.</summary>
    public static string ResolveLanguage(string? language)
    {
        var code = language?.Trim();
        return !string.IsNullOrEmpty(code)
               && (code.Equals(English, StringComparison.OrdinalIgnoreCase)
                   || code.StartsWith(English + "-", StringComparison.OrdinalIgnoreCase))
            ? English
            : Italian;
    }

    /// <summary>The facts of <paramref name="property"/> in <paramref name="language"/>, in the order of the topics. <paramref name="profile"/> is what the host wrote for the page.</summary>
    public static IReadOnlyList<ListingFact> Build(Property property, SitePropertyProfileDocument? profile, string? language)
    {
        ArgumentNullException.ThrowIfNull(property);

        var en = ResolveLanguage(language) == English;
        profile ??= SitePropertyProfileJson.Empty;
        var amenities = property.Amenities ?? [];
        var facts = new List<ListingFact>();

        void Add(KnowledgeTopic topic, string? text, string source)
        {
            if (!string.IsNullOrWhiteSpace(text))
                facts.Add(new ListingFact(topic, text.Trim(), source));
        }

        // Check-in: the window and how it works.
        Add(KnowledgeTopic.CheckIn, CheckInWindow(property.CheckInFrom, property.CheckInTo, en), ListingFactSources.CheckInWindow);
        Add(KnowledgeTopic.CheckIn, Framed(en ? "How check-in works: " : "Come funziona il check-in: ", Written(profile.CheckInMethod, en)), ListingFactSources.CheckInMethod);

        // Check-out.
        if (property.CheckOutBy is { } checkOutBy)
        {
            Add(
                KnowledgeTopic.CheckOut,
                en ? $"Check-out is by {Time(checkOutBy)}." : $"Il check-out va fatto entro le {Time(checkOutBy)}.",
                ListingFactSources.CheckOutBy);
        }

        // Wi-Fi: only that it is there. The password is never read; it can only be a custom entry.
        if (amenities.Contains(PropertyAmenity.WiFi))
            Add(KnowledgeTopic.WiFi, en ? "The property has Wi-Fi." : "Nella struttura c'è il Wi-Fi.", ListingFactSources.Amenities);

        // Parking: the kinds the host ticked, named as the booking site names them.
        var parking = new[] { PropertyAmenity.FreeParking, PropertyAmenity.PaidParking, PropertyAmenity.Garage }
            .Where(amenities.Contains)
            .Select(amenity => Label(amenity, en))
            .ToList();
        if (parking.Count > 0)
            Add(KnowledgeTopic.Parking, (en ? "Parking: " : "Parcheggio: ") + string.Join(", ", parking) + ".", ListingFactSources.Amenities);

        // House rules: the text, the note, and the answers of the wizard about smoking, parties and children.
        Add(KnowledgeTopic.HouseRules, Framed(en ? "House rules: " : "Regole della casa: ", property.HouseRules), ListingFactSources.HouseRules);
        Add(KnowledgeTopic.HouseRules, Framed(en ? "Note on the rules: " : "Nota sulle regole: ", Written(profile.RulesNote, en)), ListingFactSources.RulesNote);
        var smoking = property.SmokingPolicy switch
        {
            SmokingPolicy.No => en ? "Smoking is not allowed." : "Fumare non è consentito.",
            SmokingPolicy.OutdoorsOnly => en ? "Smoking is allowed outdoors only." : "Si può fumare solo all'aperto.",
            SmokingPolicy.Yes => en ? "Smoking is allowed." : "Fumare è consentito.",
            _ => null,
        };
        Add(KnowledgeTopic.HouseRules, smoking, ListingFactSources.SmokingPolicy);
        var parties = property.PartiesPolicy switch
        {
            PartiesPolicy.No => en ? "Parties and events are not allowed." : "Feste ed eventi non sono ammessi.",
            PartiesPolicy.OnRequest => en ? "Parties and events are allowed only on request." : "Feste ed eventi sono ammessi solo su richiesta.",
            _ => null,
        };
        Add(KnowledgeTopic.HouseRules, parties, ListingFactSources.PartiesPolicy);
        var children = property.ChildrenPolicy switch
        {
            ChildrenPolicy.No => en ? "The house is not suitable for children." : "La casa non è adatta ai bambini.",
            ChildrenPolicy.OnRequest => en ? "Children are allowed on request." : "I bambini sono ammessi su richiesta.",
            ChildrenPolicy.Yes => en ? "Children are welcome." : "I bambini sono benvenuti.",
            // The older answer of the site profile, until the host gives the structured one.
            _ => property.ChildrenWelcome switch
            {
                true => en ? "Children are welcome." : "I bambini sono benvenuti.",
                false => en ? "The house is not suitable for children." : "La casa non è adatta ai bambini.",
                null => null,
            },
        };
        Add(KnowledgeTopic.HouseRules, children, ListingFactSources.ChildrenPolicy);

        // Hours: the quiet hours.
        if (property.QuietHoursFrom is { } quietFrom && property.QuietHoursTo is { } quietTo)
        {
            Add(
                KnowledgeTopic.Hours,
                en ? $"Quiet hours are from {Time(quietFrom)} to {Time(quietTo)}." : $"Gli orari di silenzio vanno dalle {Time(quietFrom)} alle {Time(quietTo)}.",
                ListingFactSources.QuietHours);
        }

        // Amenities: the labels of the catalog, in its order.
        var labels = AmenityCatalog.Entries
            .Where(entry => amenities.Contains(entry.Amenity))
            .Select(entry => en ? entry.LabelEn : entry.LabelIt)
            .ToList();
        if (labels.Count > 0)
            Add(KnowledgeTopic.Amenities, (en ? "Amenities: " : "Servizi disponibili: ") + string.Join(", ", labels) + ".", ListingFactSources.Amenities);

        // Directions: where it is and what is around it.
        Add(KnowledgeTopic.Directions, AddressFact(property, en), ListingFactSources.Address);
        Add(KnowledgeTopic.Directions, Framed(en ? "Area: " : "Zona: ", Written(profile.Zone, en)), ListingFactSources.Zone);
        Add(KnowledgeTopic.Directions, Framed(en ? "Nearby: " : "Nelle vicinanze: ", Written(profile.Near, en)), ListingFactSources.Near);
        Add(KnowledgeTopic.Directions, Framed(en ? "Floor: " : "Piano: ", Written(profile.FloorNote, en)), ListingFactSources.FloorNote);
        Add(KnowledgeTopic.Directions, Framed(en ? "Outdoor spaces: " : "Spazi esterni: ", Written(profile.OutdoorNote, en)), ListingFactSources.OutdoorNote);

        // Pets: the answer of the wizard, or the amenity of a listing that has not been given one.
        var pets = property.PetsPolicy switch
        {
            PetsPolicy.No => en ? "Pets are not allowed." : "Gli animali non sono ammessi.",
            PetsPolicy.OnRequest => en ? "Pets are allowed on request." : "Gli animali sono ammessi su richiesta.",
            PetsPolicy.Yes => en ? "Pets are welcome." : "Gli animali sono benvenuti.",
            _ => amenities.Contains(PropertyAmenity.PetFriendly) ? (en ? "Pets are welcome." : "Gli animali sono benvenuti.") : null,
        };
        Add(KnowledgeTopic.Pets, pets, property.PetsPolicy is null ? ListingFactSources.Amenities : ListingFactSources.PetsPolicy);

        // One sentence of the amenities that the wizard answers above did not already say: smoking, for a listing with no structured answer.
        if (property.SmokingPolicy is null && amenities.Contains(PropertyAmenity.SmokingAllowed))
            Add(KnowledgeTopic.HouseRules, en ? "Smoking is allowed." : "Fumare è consentito.", ListingFactSources.Amenities);

        // The topics in their order, each topic's facts in the order they were built.
        return [.. facts.OrderBy(fact => (int)fact.Topic)];
    }

    /// <summary>The topics (but the FAQs) that are in neither <paramref name="topicsWithData"/>: the list the wizard turns into questions for the host.</summary>
    public static IReadOnlyList<KnowledgeTopic> TopicsWithoutData(IEnumerable<KnowledgeTopic> topicsWithData)
    {
        ArgumentNullException.ThrowIfNull(topicsWithData);

        var covered = topicsWithData.ToHashSet();
        return [.. Topics.Where(topic => !covered.Contains(topic))];
    }

    private static string? CheckInWindow(TimeOnly? from, TimeOnly? to, bool en) => (from, to) switch
    {
        ({ } a, { } b) => en ? $"Check-in is possible from {Time(a)} to {Time(b)}." : $"Il check-in è possibile dalle {Time(a)} alle {Time(b)}.",
        ({ } a, null) => en ? $"Check-in is possible from {Time(a)} onwards." : $"Il check-in è possibile a partire dalle {Time(a)}.",
        (null, { } b) => en ? $"Check-in is possible until {Time(b)}." : $"Il check-in è possibile fino alle {Time(b)}.",
        _ => null,
    };

    private static string? AddressFact(Property property, bool en)
    {
        var street = string.Join(", ", new[] { property.Address, property.Unit, property.City }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return street.Length == 0 ? null : en ? $"The address is {street}." : $"L'indirizzo è {street}.";
    }

    /// <summary>The text of a profile field in the language asked, Italian when the host wrote no English one; <c>null</c> when there is none.</summary>
    private static string? Written(LocalizedText? text, bool en) =>
        text is null ? null : en && !string.IsNullOrWhiteSpace(text.En) ? text.En : text.It;

    private static string? Framed(string frame, string? text) => string.IsNullOrWhiteSpace(text) ? null : frame + text.Trim();

    private static string Label(PropertyAmenity amenity, bool en)
    {
        var entry = AmenityCatalog.Entries.FirstOrDefault(candidate => candidate.Amenity == amenity);
        return entry is null ? amenity.ToString() : en ? entry.LabelEn : entry.LabelIt;
    }

    // A TimeOnly takes only the specifiers of a time.
    private static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
