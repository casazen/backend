using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Pricing;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The facts of a property that reach the AI to price it (WIZ-AI-PRICE): numbers, codes and the comune of the official list. Never the
/// name, the address, the description, the house rules or anything the host typed, nor anything about a person: what a host writes in
/// a free field cannot reach a provider, and cannot steer the answer either.
/// </summary>
/// <param name="AccommodationType">The kind of place in words, <c>null</c> when the host never said.</param>
/// <param name="Comune">The comune, <c>null</c> when the property has none.</param>
/// <param name="Province">Its province (plate code).</param>
/// <param name="Region">Its region.</param>
/// <param name="Setting">The setting in words (campagna, costa, centro storico), <c>null</c> when the host never said.</param>
/// <param name="Guests">Guests the property takes; 0 = not set.</param>
/// <param name="Bedrooms">Bedrooms; 0 is a studio.</param>
/// <param name="Beds">Beds, <c>null</c> when the host never said.</param>
/// <param name="Bathrooms">Bathrooms; 0 = not set.</param>
/// <param name="AreaSqm">Living area, <c>null</c> when the host never said.</param>
/// <param name="Amenities">The amenities that weigh on a price, in words.</param>
public sealed record PriceSuggestionFacts(
    string? AccommodationType,
    string? Comune,
    string? Province,
    string? Region,
    string? Setting,
    int Guests,
    int Bedrooms,
    int? Beds,
    int Bathrooms,
    int? AreaSqm,
    IReadOnlyList<string> Amenities);

/// <summary>
/// The prompt of the nightly price (WIZ-AI-PRICE), in Italian, with the figures of the similar homes when there are enough of them. It
/// asks for one JSON object (<see cref="PriceSuggestionAnswerParser"/>) and says what the AI may not do: invent data, name other homes.
/// A new wording is a new <see cref="PromptVersion"/>, so no answer written for the old one is reused.
/// </summary>
public static class ListingPriceSuggestionPrompt
{
    /// <summary>Version of the wording; part of the key of the cached answers.</summary>
    public const string PromptVersion = "price-v1";

    /// <summary>
    /// Amenities that say nothing about the price (the safety devices the law asks for, the rules of the house, how luggage is kept):
    /// they are left out of the prompt.
    /// </summary>
    private static readonly HashSet<PropertyAmenity> NotPriced =
    [
        PropertyAmenity.SmokeDetector,
        PropertyAmenity.CarbonMonoxideDetector,
        PropertyAmenity.FireExtinguisher,
        PropertyAmenity.FirstAidKit,
        PropertyAmenity.SmokingAllowed,
        PropertyAmenity.LongTermStaysAllowed,
        PropertyAmenity.LuggageDropOffAllowed,
    ];

    /// <summary>The facts of <paramref name="property"/> for the prompt; <paramref name="comune"/> is the one of its ISTAT code, when it has one.</summary>
    public static PriceSuggestionFacts FactsOf(Property property, Comune? comune)
    {
        ArgumentNullException.ThrowIfNull(property);

        var amenities = property.Amenities
            .Where(amenity => !NotPriced.Contains(amenity))
            .Distinct()
            .Select(AmenityLabel)
            .ToList();

        var name = comune?.Name ?? (string.IsNullOrWhiteSpace(property.City) ? null : property.City.Trim());
        return new PriceSuggestionFacts(
            property.AccommodationType is { } type ? TypeLabel(type) : null,
            name,
            string.IsNullOrWhiteSpace(comune?.ProvinceCode) ? null : comune.ProvinceCode,
            string.IsNullOrWhiteSpace(comune?.RegionName) ? null : comune.RegionName,
            property.AreaKind is { } kind ? SettingLabel(kind) : null,
            property.MaxGuests,
            property.Bedrooms,
            property.Beds,
            property.Bathrooms,
            property.AreaSqm,
            amenities);
    }

    /// <summary>The codes (<see cref="PriceSuggestionFields"/>) of the facts the prompt really carries: what the host did not say is not listed.</summary>
    public static IReadOnlyList<string> UsedFields(PriceSuggestionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var used = new List<string>();
        if (facts.AccommodationType is not null)
            used.Add(PriceSuggestionFields.Type);
        if (facts.Guests > 0)
            used.Add(PriceSuggestionFields.Guests);
        used.Add(PriceSuggestionFields.Bedrooms);
        if (facts.Beds is > 0)
            used.Add(PriceSuggestionFields.Beds);
        if (facts.Bathrooms > 0)
            used.Add(PriceSuggestionFields.Bathrooms);
        if (facts.AreaSqm is > 0)
            used.Add(PriceSuggestionFields.Area);
        if (facts.Amenities.Count > 0)
            used.Add(PriceSuggestionFields.Amenities);
        if (facts.Setting is not null)
            used.Add(PriceSuggestionFields.Setting);
        return used;
    }

    /// <summary>The whole prompt; <paramref name="language"/> (<c>it</c> or <c>en</c>) is the language of the explanation.</summary>
    public static string Build(PriceSuggestionFacts facts, MarketPriceStats? market, PricePositioning positioning, string language)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var lines = new List<string>
        {
            "Sei un revenue manager esperto di affitti brevi in Italia. Stima il prezzo base per notte di una casa vacanze "
            + "che il proprietario sta per pubblicare su CasaZen.",
            string.Empty,
            "Dati della casa (forniti dal proprietario):",
        };

        if (facts.AccommodationType is not null)
            lines.Add($"- Tipo: {facts.AccommodationType}");

        lines.Add($"- Posizione: {Place(facts)}");

        if (facts.Setting is not null)
            lines.Add($"- Contesto: {facts.Setting}");
        if (facts.Guests > 0)
            lines.Add(Line("Ospiti", facts.Guests));

        lines.Add(Line("Camere da letto", facts.Bedrooms));

        if (facts.Beds is > 0)
            lines.Add(Line("Letti", facts.Beds.Value));
        if (facts.Bathrooms > 0)
            lines.Add(Line("Bagni", facts.Bathrooms));
        if (facts.AreaSqm is > 0)
            lines.Add($"- Superficie: {facts.AreaSqm.Value.ToString(CultureInfo.InvariantCulture)} m²");
        if (facts.Amenities.Count > 0)
            lines.Add($"- Dotazioni: {string.Join(", ", facts.Amenities)}");

        lines.Add(string.Empty);
        lines.Add(MarketBlock(facts, market));
        lines.Add(string.Empty);
        lines.Add($"Posizionamento scelto dal proprietario: {PositioningText(positioning)}.");
        lines.Add(string.Empty);
        lines.AddRange(
        [
            "Cosa devi fare:",
            "1. Stima il prezzo base per notte: quello di una notte media dell'anno, non di alta né di bassa stagione "
            + "(a stagioni e festività penserà il proprietario in un secondo momento).",
            "2. Indica l'intervallo tipico che chiedono le case simili nella stessa zona (rangeMin e rangeMax).",
            "3. Suggerisci il costo di pulizia (una volta a soggiorno), il supplemento weekend in percentuale (notti di venerdì "
            + "e sabato) e il soggiorno minimo in notti.",
            "4. Spiega in al massimo 3 frasi brevi perché: nomina le caratteristiche che pesano di più (zona, dimensioni, "
            + "dotazioni) e, se li hai, i dati delle case simili.",
            string.Empty,
            "Regole:",
            "- Non inventare dati: se non conosci bene la zona, scegli un valore prudente e dillo nella motivazione.",
            "- Non nominare altre strutture, altri annunci o altri proprietari.",
            $"- Scrivi la motivazione in {(IsEnglish(language) ? "inglese" : "italiano")}, rivolgendoti direttamente al proprietario, "
            + "senza elenchi né markdown.",
            "- Rispondi SOLO con un oggetto JSON, senza testo prima o dopo, con queste chiavi e nient'altro:",
            "{\"nightlyRate\": 0, \"rangeMin\": 0, \"rangeMax\": 0, \"cleaningFee\": 0, \"weekendSurchargePercent\": 0, "
            + "\"minNights\": 1, \"rationale\": \"\"}",
            "Tutti i numeri sono interi: i prezzi in euro, il supplemento weekend da 0 a 50, il soggiorno minimo da 1 a 7.",
        ]);

        return string.Join('\n', lines);
    }

    private static string MarketBlock(PriceSuggestionFacts facts, MarketPriceStats? market)
    {
        var where = facts.Comune is null ? "in questa zona" : $"a {facts.Comune}";
        if (market is null)
        {
            return $"Su CasaZen non ci sono ancora abbastanza case pubblicate {where} per un confronto: usa la tua conoscenza "
                + "generale dei prezzi degli affitti brevi in questa zona e scrivi nella motivazione che è una stima generale.";
        }

        var size = market.SimilarSize
            ? "di dimensioni simili a questa"
            : "di varie dimensioni (non tutte confrontabili con questa)";
        return $"Dati reali di CasaZen: {market.Count.ToString(CultureInfo.InvariantCulture)} case {size} pubblicate {where} "
            + $"chiedono per notte da {Euro(market.Min)} a {Euro(market.Max)} (primo quartile {Euro(market.P25)}, mediana "
            + $"{Euro(market.Median)}, terzo quartile {Euro(market.P75)}). Usali come riferimento principale e adattali al "
            + "confronto con questa casa.";
    }

    private static string PositioningText(PricePositioning positioning) => positioning switch
    {
        PricePositioning.Lower => "più economico delle case simili (circa il 10% in meno), per ricevere più prenotazioni",
        PricePositioning.Higher => "più alto delle case simili (circa il 10% in più), perché la casa si distingue",
        _ => "in linea con le case simili della zona",
    };

    private static string Place(PriceSuggestionFacts facts)
    {
        if (facts.Comune is null)
            return "non indicata";

        var province = facts.Province is null ? string.Empty : $" ({facts.Province})";
        var region = facts.Region is null ? string.Empty : $", {facts.Region}";
        return $"{facts.Comune}{province}{region}";
    }

    private static string Line(string label, int value) => $"- {label}: {value.ToString(CultureInfo.InvariantCulture)}";

    private static string Euro(decimal value) => $"{Math.Round(value).ToString("0", CultureInfo.InvariantCulture)} €";

    private static bool IsEnglish(string language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

    private static string TypeLabel(AccommodationType type) => type switch
    {
        AccommodationType.Apartment => "appartamento",
        AccommodationType.HouseOrVilla => "casa o villa",
        AccommodationType.TrulloOrMasseria => "trullo o masseria",
        AccommodationType.LoftOrPenthouse => "loft o attico",
        AccommodationType.RoomOrSuite => "stanza o suite",
        _ => type.ToString(),
    };

    private static string SettingLabel(PropertyAreaKind kind) => kind switch
    {
        PropertyAreaKind.Country => "campagna",
        PropertyAreaKind.Coast => "costa (sul mare o a breve distanza)",
        PropertyAreaKind.Historic => "centro storico",
        _ => kind.ToString(),
    };

    /// <summary>An amenity in Italian words; the ones that weigh most on a price are named, the others by their English name.</summary>
    public static string AmenityLabel(PropertyAmenity amenity) => amenity switch
    {
        PropertyAmenity.WiFi => "WiFi",
        PropertyAmenity.TV => "TV",
        PropertyAmenity.Workspace => "angolo per lavorare",
        PropertyAmenity.AirConditioning => "aria condizionata",
        PropertyAmenity.Heating => "riscaldamento",
        PropertyAmenity.Fireplace => "camino",
        PropertyAmenity.Kitchen => "cucina",
        PropertyAmenity.Dishwasher => "lavastoviglie",
        PropertyAmenity.Washer => "lavatrice",
        PropertyAmenity.Dryer => "asciugatrice",
        PropertyAmenity.Pool => "piscina",
        PropertyAmenity.HotTub => "vasca idromassaggio",
        PropertyAmenity.Garden => "giardino",
        PropertyAmenity.Patio => "patio",
        PropertyAmenity.Balcony => "balcone",
        PropertyAmenity.BBQGrill => "barbecue",
        PropertyAmenity.BeachAccess => "accesso alla spiaggia",
        PropertyAmenity.Terrace => "terrazza",
        PropertyAmenity.FreeParking => "parcheggio gratuito",
        PropertyAmenity.PaidParking => "parcheggio a pagamento",
        PropertyAmenity.Garage => "garage",
        PropertyAmenity.EVCharger => "colonnina di ricarica elettrica",
        PropertyAmenity.Elevator => "ascensore",
        PropertyAmenity.Gym => "palestra",
        PropertyAmenity.Sauna => "sauna",
        PropertyAmenity.PetFriendly => "animali ammessi",
        PropertyAmenity.SelfCheckIn => "self check-in",
        PropertyAmenity.PrivateEntrance => "ingresso privato",
        PropertyAmenity.Safe => "cassaforte",
        _ => amenity.ToString(),
    };
}
