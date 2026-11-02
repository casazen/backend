using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the AI is told about the property whose listing it writes: only facts that are not personal data (FD-21). The comune, its province
/// and its region stand for the position: never the address, the unit, the name the host gave the property or anything about the host.
/// </summary>
public sealed record ListingTextFacts(
    AccommodationType? Type,
    int MaxGuests,
    int Bedrooms,
    int? Beds,
    int Bathrooms,
    int? AreaSqm,
    IReadOnlyList<PropertyAmenity> Amenities,
    string? Comune,
    string? Province,
    string? Region,
    PropertyAreaKind? AreaKind,
    int? FloorLevel,
    PetsPolicy? Pets,
    SmokingPolicy? Smoking,
    PartiesPolicy? Parties,
    TimeOnly? CheckInFrom,
    TimeOnly? CheckOutBy,
    bool? ChildrenWelcome,
    int Photos,
    IReadOnlyList<SimilarListingFacts> SimilarListings);

/// <summary>A listing published on CasaZen in the same comune: its public headline (when it has one) and its data, nothing of its host.</summary>
public sealed record SimilarListingFacts(
    string? Title,
    AccommodationType? Type,
    int MaxGuests,
    int Bedrooms,
    int Bathrooms,
    int? AreaSqm,
    IReadOnlyList<PropertyAmenity> Amenities);

/// <summary>
/// The versioned prompt of «Suggerisci un testo» (the wizard «Aggiungi immobile»): the rules of the answer (true to the data and to the photos,
/// no invented amenity, position or distance, a fixed structure and length) and the facts of the property. <see cref="Version"/> is part of the
/// cache key: change it whenever the text below changes, so an answer written with the old rules is not served again. The answer is read back
/// by <see cref="TryParseAnswer"/>.
/// </summary>
public static partial class ListingTextPrompt
{
    public const string Version = "listing-it-2026-11-v1";

    /// <summary>A little creativity: the text is the host's to correct, but it should not read like a form.</summary>
    public const double Temperature = 0.6;

    /// <summary>A description of up to 1,200 characters and a headline, in JSON: about 500 tokens; the rest is a margin.</summary>
    public const int MaxCompletionTokens = 1_200;

    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public const string SystemPrompt =
        "Sei un copywriter esperto di affitti brevi in Italia. Scrivi il titolo e la descrizione dell'annuncio di un alloggio, che " +
        "l'host pubblica sul proprio sito di prenotazione diretta dopo averli riletti.\n\n" +
        "Regole di verità:\n" +
        "- Usa solo i dati dell'alloggio che ricevi e ciò che si vede con chiarezza nelle foto. Non inventare dotazioni, servizi, " +
        "distanze, piani, viste, ristrutturazioni o arredi.\n" +
        "- Le foto, se le ricevi: descrivi gli ambienti, lo stile, la luce, i materiali e gli spazi esterni che si vedono davvero. Una " +
        "dotazione che si vede chiaramente (un terrazzo, una cucina attrezzata, un camino) puoi citarla; una che non vedi e che non è " +
        "tra i dati, no. Non descrivere persone, oggetti personali, documenti o targhe e non scrivere \"come si vede nelle foto\". Se non " +
        "ricevi immagini, non descrivere arredi, stile, colori o viste.\n" +
        "- La posizione: conosci il comune ma non l'indirizzo. Parla del comune e del suo territorio (cosa offre, in quale stagione, a chi " +
        "è adatto) senza dire che l'alloggio è in centro, sul mare, sul lago o vicino a un luogo preciso e senza distanze o tempi di " +
        "percorrenza, a meno che la zona sia nei dati o si veda chiaramente nelle foto (per esempio il lago dalla finestra).\n" +
        "- Gli annunci simili servono a capire cosa cercano gli ospiti in quel comune e a far risaltare ciò che distingue questo " +
        "alloggio: non copiarne frasi o titoli.\n" +
        "- Non citare prezzi, sconti, il codice CIN, contatti, link, recensioni o premi, e nessuna promessa che l'host non può garantire.\n\n" +
        "Stile:\n" +
        "- Italiano corretto e naturale, tono caldo ma concreto; rivolgiti agli ospiti con il voi o in forma impersonale.\n" +
        "- Niente superlativi vuoti e cliché (incantevole, perla, angolo di paradiso, immerso nel verde, a due passi se non è un fatto).\n" +
        "- Frasi brevi e chiare. Niente emoji, elenchi puntati, titoletti, maiuscole enfatiche o markdown.\n\n" +
        "Struttura:\n" +
        "- Titolo: al massimo 50 caratteri (mai più di 60), concreto: il tipo di alloggio, un elemento che lo distingue (dai dati o dalle " +
        "foto) e il comune. Senza punto finale né punti esclamativi.\n" +
        "- Descrizione: tra 700 e 1.200 caratteri, in 3 o 4 paragrafi separati da una riga vuota: 1) un'apertura di una o due frasi su " +
        "cosa rende piacevole il soggiorno; 2) gli spazi (camere, letti, bagni, zona giorno, cucina, esterni); 3) il comune e il " +
        "territorio, e per chi è ideale il soggiorno; 4) le informazioni pratiche dai dati: quanti ospiti, le dotazioni principali, le " +
        "regole indicate.\n\n" +
        "Rispondi solo con un oggetto JSON, senza testo prima o dopo: {\"title\": \"...\", \"description\": \"...\"}. " +
        "Nella descrizione separa i paragrafi con \\n\\n.";

    /// <summary>The request: the facts of the property, the photos attached and the listings of the same comune.</summary>
    public static string BuildPrompt(ListingTextFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var text = new StringBuilder();
        text.AppendLine("Dati dell'alloggio:");
        text.AppendLine($"- Tipo: {TypeLabel(facts.Type)}");
        if (!string.IsNullOrWhiteSpace(facts.Comune))
            text.AppendLine($"- Comune: {Place(facts.Comune, facts.Province, facts.Region)}");
        if (facts.AreaKind is { } areaKind)
            text.AppendLine($"- Zona indicata dall'host: {AreaKindLabel(areaKind)}");
        if (facts.FloorLevel is { } floor)
            text.AppendLine($"- Piano: {(floor == 0 ? "terra" : floor.ToString(Italian))}");
        text.AppendLine($"- Ospiti al massimo: {facts.MaxGuests}");
        text.AppendLine(facts.Bedrooms > 0 ? $"- Camere da letto: {facts.Bedrooms}" : "- Camere da letto: nessuna (monolocale)");
        if (facts.Beds is > 0)
            text.AppendLine($"- Letti: {facts.Beds}");
        text.AppendLine($"- Bagni: {facts.Bathrooms}");
        if (facts.AreaSqm is > 0)
            text.AppendLine($"- Superficie: {facts.AreaSqm} m²");
        text.AppendLine(facts.Amenities.Count > 0
            ? $"- Dotazioni indicate dall'host: {AmenityList(facts.Amenities)}"
            : "- Dotazioni: non ancora indicate dall'host (non citarne nessuna che non si veda chiaramente nelle foto)");
        foreach (var rule in Rules(facts))
            text.AppendLine($"- {rule}");

        text.AppendLine();
        text.AppendLine(facts.Photos > 0
            ? $"Foto allegate: {facts.Photos}, nell'ordine della galleria (la prima è la copertina)."
            : "Nessuna foto: scrivi solo dai dati.");

        text.AppendLine();
        if (facts.SimilarListings.Count == 0)
        {
            text.AppendLine("Nessun altro annuncio è pubblicato su CasaZen in questo comune.");
        }
        else
        {
            text.AppendLine("Annunci pubblicati su CasaZen nello stesso comune (non copiarli):");
            var index = 1;
            foreach (var listing in facts.SimilarListings)
                text.AppendLine($"{index++}. {Describe(listing)}");
        }

        text.AppendLine();
        text.Append("Scrivi ora il titolo e la descrizione.");
        return text.ToString();
    }

    /// <summary>
    /// Reads the headline and the description from the answer of the AI: the JSON object asked for, also inside a code fence or with words
    /// around it. The texts come back cleaned (no markdown emphasis, no quotes around the headline, paragraphs separated by one blank line)
    /// and within the limits of the fields, cut at the end of a word, a sentence or a paragraph. False when either is missing.
    /// </summary>
    public static bool TryParseAnswer(string? content, out string title, out string description)
    {
        title = string.Empty;
        description = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        try
        {
            using var document = JsonDocument.Parse(content[start..(end + 1)]);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            title = CleanTitle(ReadString(document.RootElement, "title", "titolo"));
            description = CleanDescription(ReadString(document.RootElement, "description", "descrizione"));
        }
        catch (JsonException)
        {
            return false;
        }

        return title.Length > 0 && description.Length > 0;
    }

    private static string ReadString(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (names.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>Quotes and closing punctuation around a headline: the field shows neither.</summary>
    private static readonly char[] TitleEdges = ['"', '\'', '«', '»', '“', '”', '.', '!', ' '];

    private static string CleanTitle(string value)
    {
        var title = Whitespace().Replace(StripEmphasis(value), " ").Trim(TitleEdges);
        return CutAtWord(title, ListingTextLimits.TitleMaxLength);
    }

    private static string CleanDescription(string value)
    {
        var lines = StripEmphasis(value).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = Whitespace().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (current.Length > 0)
                current.Append(' ');
            current.Append(line);
        }

        Flush();
        return CutAtParagraphOrSentence(paragraphs, ListingTextLimits.DescriptionMaxLength);

        void Flush()
        {
            if (current.Length > 0)
                paragraphs.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>Whole paragraphs while they fit; the first one that does not fit is cut at its last whole sentence (or word).</summary>
    private static string CutAtParagraphOrSentence(IReadOnlyList<string> paragraphs, int max)
    {
        var text = new StringBuilder();
        foreach (var paragraph in paragraphs)
        {
            var separator = text.Length == 0 ? string.Empty : "\n\n";
            if (text.Length + separator.Length + paragraph.Length <= max)
            {
                text.Append(separator).Append(paragraph);
                continue;
            }

            var room = max - text.Length - separator.Length;
            var cut = CutAtSentence(paragraph, room);
            if (cut.Length > 0)
                text.Append(separator).Append(cut);
            break;
        }

        return text.ToString();
    }

    private static string CutAtSentence(string text, int max)
    {
        if (max <= 0)
            return string.Empty;
        if (text.Length <= max)
            return text;

        var head = text[..max];
        var sentenceEnd = head.LastIndexOfAny(['.', '!', '?']);
        return sentenceEnd > max / 3 ? head[..(sentenceEnd + 1)].Trim() : CutAtWord(text, max);
    }

    private static string CutAtWord(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var head = text[..max];
        var space = head.LastIndexOf(' ');
        return (space > max / 2 ? head[..space] : head).TrimEnd(' ', ',', ';', ':', '-', '·').Trim();
    }

    private static string StripEmphasis(string value) => Emphasis().Replace(value ?? string.Empty, string.Empty);

    private static IEnumerable<string> Rules(ListingTextFacts facts)
    {
        if (facts.Pets is { } pets)
        {
            yield return pets switch
            {
                PetsPolicy.Yes => "Animali: ammessi",
                PetsPolicy.OnRequest => "Animali: su richiesta",
                _ => "Animali: non ammessi",
            };
        }

        if (facts.Smoking is { } smoking)
        {
            yield return smoking switch
            {
                SmokingPolicy.Yes => "Fumo: ammesso",
                SmokingPolicy.OutdoorsOnly => "Fumo: solo all'aperto",
                _ => "Fumo: non ammesso",
            };
        }

        if (facts.Parties is { } parties)
            yield return parties == PartiesPolicy.OnRequest ? "Feste ed eventi: su richiesta" : "Feste ed eventi: non ammessi";

        if (facts.CheckInFrom is { } checkIn)
            yield return $"Check-in dalle {checkIn.ToString("HH:mm", Italian)}";
        if (facts.CheckOutBy is { } checkOut)
            yield return $"Check-out entro le {checkOut.ToString("HH:mm", Italian)}";
        if (facts.ChildrenWelcome == true)
            yield return "Bambini: benvenuti";
    }

    private static string Describe(SimilarListingFacts listing)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(listing.Title))
            parts.Add($"«{listing.Title.Trim()}»");
        parts.Add(TypeLabel(listing.Type));
        parts.Add(listing.MaxGuests == 1 ? "1 ospite" : $"{listing.MaxGuests} ospiti");
        parts.Add(listing.Bedrooms switch
        {
            0 => "monolocale",
            1 => "1 camera",
            _ => $"{listing.Bedrooms} camere",
        });
        parts.Add(listing.Bathrooms == 1 ? "1 bagno" : $"{listing.Bathrooms} bagni");
        if (listing.AreaSqm is > 0)
            parts.Add($"{listing.AreaSqm} m²");
        if (listing.Amenities.Count > 0)
            parts.Add(AmenityList(listing.Amenities));
        return string.Join(" · ", parts);
    }

    private static string Place(string comune, string? province, string? region)
    {
        var place = comune.Trim();
        if (!string.IsNullOrWhiteSpace(province))
            place += $" (provincia {province.Trim()})";
        if (!string.IsNullOrWhiteSpace(region))
            place += $", {region.Trim()}";
        return place;
    }

    private static string AmenityList(IReadOnlyList<PropertyAmenity> amenities) =>
        string.Join(", ", amenities.Distinct().Select(AmenityLabel));

    private static string AmenityLabel(PropertyAmenity amenity) =>
        AmenityCatalog.Entries.FirstOrDefault(entry => entry.Amenity == amenity)?.LabelIt ?? amenity.ToString();

    private static string TypeLabel(AccommodationType? type) => type switch
    {
        AccommodationType.Apartment => "Appartamento",
        AccommodationType.HouseOrVilla => "Casa o villa",
        AccommodationType.TrulloOrMasseria => "Trullo o masseria",
        AccommodationType.LoftOrPenthouse => "Loft o attico",
        AccommodationType.RoomOrSuite => "Stanza o suite",
        _ => "Alloggio",
    };

    private static string AreaKindLabel(PropertyAreaKind kind) => kind switch
    {
        PropertyAreaKind.Country => "campagna",
        PropertyAreaKind.Coast => "costa, sul mare o a breve distanza",
        PropertyAreaKind.Historic => "centro storico",
        _ => kind.ToString(),
    };

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\*\*|__|`")]
    private static partial Regex Emphasis();
}
