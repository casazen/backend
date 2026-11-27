namespace Casazen.Core.Regulatory;

/// <summary>
/// Citizenship adjectives shown to the guest. The Alloggiati table "Stati" stores the country (ITALIA); the guest
/// chooses a citizenship (Italiano). The code sent to the Questura stays the official state code. Unknown countries
/// keep the official description: nothing is invented for them.
/// </summary>
public static class ItalianCitizenshipNames
{
    private readonly record struct Entry(string DescriptionKey, string Label, string[] Aliases);

    /// <summary>Description key is <see cref="AlloggiatiRecordRules.NormalizeDescription"/> of the country name.</summary>
    private static readonly Entry[] Table =
    [
        new("ITALIA", "Italiano", ["ITALIANA", "ITALIANO", "ITALIAN"]),
        new("FRANCIA", "Francese", ["FRANCESE"]),
        new("GERMANIA", "Tedesca", ["TEDESCA", "TEDESCO"]),
        new("SPAGNA", "Spagnola", ["SPAGNOLA", "SPAGNOLO"]),
        new("REGNOUNITO", "Britannica", ["BRITANNICA", "BRITANNICO"]),
        new("STATIUNITI", "Statunitense", ["STATUNITENSE"]),
        new("STATIUNITIDAMERICA", "Statunitense", ["STATUNITENSE"]),
        new("SVIZZERA", "Svizzera", ["SVIZZERA", "SVIZZERO"]),
        new("AUSTRIA", "Austriaca", ["AUSTRIACA", "AUSTRIACO"]),
        new("BELGIO", "Belga", ["BELGA"]),
        new("PAESIBASSI", "Olandese", ["OLANDESE"]),
        new("OLANDA", "Olandese", ["OLANDESE"]),
        new("PORTOGALLO", "Portoghese", ["PORTOGHESE"]),
        new("GRECIA", "Greca", ["GRECA", "GRECO"]),
        new("POLONIA", "Polacca", ["POLACCA", "POLACCO"]),
        new("ROMANIA", "Rumena", ["RUMENA", "RUMENO"]),
        new("BULGARIA", "Bulgara", ["BULGARA", "BULGARO"]),
        new("UNGHERIA", "Ungherese", ["UNGHERESE"]),
        new("CROAZIA", "Croata", ["CROATA"]),
        new("SLOVENIA", "Slovena", ["SLOVENA", "SLOVENO"]),
        new("SLOVACCHIA", "Slovacca", ["SLOVACCA", "SLOVACCO"]),
        new("REPUBBLICACECA", "Ceca", ["CECA", "CECO"]),
        new("SVEZIA", "Svedese", ["SVEDESE"]),
        new("NORVEGIA", "Norvegese", ["NORVEGESE"]),
        new("DANIMARCA", "Danese", ["DANESE"]),
        new("FINLANDIA", "Finlandese", ["FINLANDESE"]),
        new("IRLANDA", "Irlandese", ["IRLANDESE"]),
        new("ALBANIA", "Albanese", ["ALBANESE"]),
        new("UCRAINA", "Ucraina", ["UCRAINA"]),
        new("RUSSIA", "Russa", ["RUSSA", "RUSSO"]),
        new("FEDERAZIONERUSSA", "Russa", ["RUSSA", "RUSSO"]),
        new("CINA", "Cinese", ["CINESE"]),
        new("GIAPPONE", "Giapponese", ["GIAPPONESE"]),
        new("INDIA", "Indiana", ["INDIANA"]),
        new("BRASILE", "Brasiliana", ["BRASILIANA", "BRASILIANO"]),
        new("ARGENTINA", "Argentina", ["ARGENTINA"]),
        new("MAROCCO", "Marocchina", ["MAROCCHINA", "MAROCCHINO"]),
        new("TUNISIA", "Tunisina", ["TUNISINA", "TUNISINO"]),
        new("EGITTO", "Egiziana", ["EGIZIANA", "EGIZIANO"]),
        new("TURCHIA", "Turca", ["TURCA", "TURCO"]),
        new("CANADA", "Canadese", ["CANADESE"]),
        new("AUSTRALIA", "Australiana", ["AUSTRALIANA", "AUSTRALIANO"]),
        new("MESSICO", "Messicana", ["MESSICANA", "MESSICANO"]),
        new("SANMARINO", "Sammarinese", ["SAMMARINESE"]),
        new("CITTADELVATICANO", "Vaticana", ["VATICANA", "VATICANO"]),
        new("VATICANO", "Vaticana", ["VATICANA", "VATICANO"]),
    ];

    private static readonly Dictionary<string, string> LabelByDescription = Table
        .GroupBy(entry => entry.DescriptionKey)
        .ToDictionary(group => group.Key, group => group.First().Label);

    private static readonly Dictionary<string, string> DescriptionByAlias = Table
        .SelectMany(entry => entry.Aliases.Select(alias => (alias, entry.DescriptionKey)))
        .GroupBy(pair => pair.alias)
        .ToDictionary(group => group.Key, group => group.First().DescriptionKey);

    /// <summary>Adjective to show for an official state description, or null when this country has none.</summary>
    public static string? LabelFor(string? normalizedDescription) =>
        normalizedDescription is not null && LabelByDescription.TryGetValue(normalizedDescription, out var label)
            ? label
            : null;

    /// <summary>
    /// Official description key of a citizenship the guest typed ("italiana", "italiano"), or null when the text is
    /// already a country name or is unknown.
    /// </summary>
    public static string? OfficialNormalizedKey(string? text)
    {
        var key = AlloggiatiRecordRules.NormalizeDescription(text);
        return key.Length > 0 && DescriptionByAlias.TryGetValue(key, out var description) ? description : null;
    }
}
