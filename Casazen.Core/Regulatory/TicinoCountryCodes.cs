using System.Globalization;
using System.Text;

namespace Casazen.Core.Regulatory;

/// <summary>
/// The ISO 3166-1 alpha-3 code of a country of the Alloggiati Web «Stati» table (casazen/backend#979): the Ticino police file
/// (notifiche.ti.ch) wants the nationality and the country of residence as alpha-3 (<c>CHE</c>, <c>ITA</c>), while the check-in keeps them
/// as an Alloggiati code and a label (<see cref="Entities.StayGuest.CitizenshipCode"/>, <see cref="Entities.StayGuest.CitizenshipName"/>).
/// </summary>
/// <remarks>
/// <para>The table was generated once from the committed Alloggiati file (<c>Data/Seeds/alloggiati/stati.csv</c>, 2026-10-09) by matching
/// each Italian name with the Italian and English names of the CLDR/ICU region data (alpha-2 to alpha-3 from the same data), then reviewed by
/// hand: the names CLDR spells differently were resolved one by one (see <c>docs/compliance/ticino-notification-format.md</c>). A code that
/// is not here is never guessed: the guest is reported as missing the nationality and the host notifies them on the portal.</para>
/// <para>Not mapped on purpose: APOLIDE (no country), BOPHUTHATSWANA and CECOSLOVACCHIA (states that no longer exist). KOSOVO has no ISO
/// code: it is mapped to <c>XKX</c>, the user-assigned code used by the European Union, which the first real upload must confirm.</para>
/// </remarks>
public static class TicinoCountryCodes
{
    private static readonly (string Code, string Description, string Alpha3)[] Rows =
    [
        ("100000301", "AFGHANISTAN", "AFG"),
        ("100000201", "ALBANIA", "ALB"),
        ("100000401", "ALGERIA", "DZA"),
        ("100000202", "ANDORRA", "AND"),
        ("100000402", "ANGOLA", "AGO"),
        ("100000733", "ANGUILLA (ISOLA)", "AIA"),
        ("100000503", "ANTIGUA E BARBUDA", "ATG"),
        ("100000302", "ARABIA SAUDITA", "SAU"),
        ("100000602", "ARGENTINA", "ARG"),
        ("100000358", "ARMENIA", "ARM"),
        ("100000701", "AUSTRALIA", "AUS"),
        ("100000203", "AUSTRIA", "AUT"),
        ("100000359", "AZERBAIGIAN", "AZE"),
        ("100000505", "BAHAMAS", "BHS"),
        ("100000304", "BAHREIN", "BHR"),
        ("100000305", "BANGLADESH", "BGD"),
        ("100000506", "BARBADOS", "BRB"),
        ("100000206", "BELGIO", "BEL"),
        ("100000507", "BELIZE", "BLZ"),
        ("100000406", "BENIN", "BEN"),
        ("100000739", "BERMUDE", "BMU"),
        ("100000306", "BHUTAN", "BTN"),
        ("100000256", "BIELORUSSIA", "BLR"),
        ("100000604", "BOLIVIA", "BOL"),
        ("100000252", "BOSNIA ED ERZEGOVINA", "BIH"),
        ("100000408", "BOTSWANA", "BWA"),
        ("100000605", "BRASILE", "BRA"),
        ("100000309", "BRUNEI DARUSSALAM", "BRN"),
        ("100000209", "BULGARIA", "BGR"),
        ("100000409", "BURKINA FASO", "BFA"),
        ("100000410", "BURUNDI", "BDI"),
        ("100000310", "CAMBOGIA", "KHM"),
        ("100000411", "CAMERUN", "CMR"),
        ("100000509", "CANADA", "CAN"),
        ("100000413", "CAPO VERDE", "CPV"),
        ("100000742", "CAYMAN (ISOLE)", "CYM"),
        ("100000743", "CHRISTMAS", "CXR"),
        ("100000415", "CIAD", "TCD"),
        ("100000606", "CILE", "CHL"),
        ("100000314", "CINA", "CHN"),
        ("100000315", "CIPRO", "CYP"),
        ("100000746", "COCOS", "CCK"),
        ("100000608", "COLOMBIA", "COL"),
        ("100000417", "COMORE", "COM"),
        ("100000418", "CONGO", "COG"),
        ("100000319", "COREA DEL NORD", "PRK"),
        ("100000320", "COREA DEL SUD", "KOR"),
        ("100000404", "COSTA D'AVORIO", "CIV"),
        ("100000513", "COSTA RICA", "CRI"),
        ("100000250", "CROAZIA", "HRV"),
        ("100000514", "CUBA", "CUB"),
        ("100000212", "DANIMARCA", "DNK"),
        ("100000515", "DOMINICA", "DMA"),
        ("100000609", "ECUADOR", "ECU"),
        ("100000419", "EGITTO", "EGY"),
        ("100000517", "EL SALVADOR", "SLV"),
        ("100000322", "EMIRATI ARABI UNITI", "ARE"),
        ("100000466", "ERITREA", "ERI"),
        ("100000247", "ESTONIA", "EST"),
        ("100000420", "ETIOPIA", "ETH"),
        ("100000755", "FAER OER", "FRO"),
        ("100000245", "FEDERAZIONE RUSSA", "RUS"),
        ("100000703", "FIGI", "FJI"),
        ("100000323", "FILIPPINE", "PHL"),
        ("100000214", "FINLANDIA", "FIN"),
        ("100000215", "FRANCIA", "FRA"),
        ("100000421", "GABON", "GAB"),
        ("100000422", "GAMBIA", "GMB"),
        ("100000360", "GEORGIA", "GEO"),
        ("110000022", "GEORGIA SUD E ISOLE SANDWICH AUSTRALI", "SGS"),
        ("100000216", "GERMANIA", "DEU"),
        ("100000423", "GHANA", "GHA"),
        ("100000518", "GIAMAICA", "JAM"),
        ("100000326", "GIAPPONE", "JPN"),
        ("100000424", "GIBUTI", "DJI"),
        ("100000327", "GIORDANIA", "JOR"),
        ("100000220", "GRECIA", "GRC"),
        ("100000519", "GRENADA", "GRD"),
        ("100000758", "GROENLANDIA", "GRL"),
        ("100000759", "GUADALUPA", "GLP"),
        ("100000760", "GUAM", "GUM"),
        ("100000523", "GUATEMALA", "GTM"),
        ("100000761", "GUAYANA FRANCESE", "GUF"),
        ("110000014", "GUERNSEY", "GGY"),
        ("100000425", "GUINEA", "GIN"),
        ("100000426", "GUINEA BISSAU", "GNB"),
        ("100000427", "GUINEA EQUATORIALE", "GNQ"),
        ("100000612", "GUYANA", "GUY"),
        ("100000524", "HAITI", "HTI"),
        ("100000525", "HONDURAS", "HND"),
        ("110000005", "HONG KONG", "HKG"),
        ("100000330", "INDIA", "IND"),
        ("100000331", "INDONESIA", "IDN"),
        ("100000332", "IRAN", "IRN"),
        ("100000333", "IRAQ", "IRQ"),
        ("100000221", "IRLANDA", "IRL"),
        ("100000223", "ISLANDA", "ISL"),
        ("100000764", "ISOLE VERGINI", "VIR"),
        ("100000334", "ISRAELE", "ISR"),
        ("100000100", "ITALIA", "ITA"),
        ("100000356", "KAZAKISTAN", "KAZ"),
        ("100000428", "KENYA", "KEN"),
        ("100000361", "KIRGHIZISTAN", "KGZ"),
        ("100000708", "KIRIBATI", "KIR"),
        ("100001002", "KOSOVO", "XKX"),
        ("100000335", "KUWAIT", "KWT"),
        ("100000765", "LA REUNION", "REU"),
        ("100000336", "LAOS", "LAO"),
        ("100000429", "LESOTHO", "LSO"),
        ("100000248", "LETTONIA", "LVA"),
        ("100000337", "LIBANO", "LBN"),
        ("100000430", "LIBERIA", "LBR"),
        ("100000431", "LIBIA", "LBY"),
        ("100000225", "LIECHTENSTEIN", "LIE"),
        ("100000249", "LITUANIA", "LTU"),
        ("100000226", "LUSSEMBURGO", "LUX"),
        ("110000003", "MACAO", "MAC"),
        ("100000253", "MACEDONIA", "MKD"),
        ("100000997", "MACEDONIA DEL NORD", "MKD"),
        ("100000432", "MADAGASCAR", "MDG"),
        ("100000434", "MALAWI", "MWI"),
        ("100000767", "MALAYSIA", "MYS"),
        ("100000339", "MALDIVE", "MDV"),
        ("100000435", "MALI", "MLI"),
        ("100000227", "MALTA", "MLT"),
        ("100000768", "MALVINE", "FLK"),
        ("100000769", "MAN", "IMN"),
        ("100000436", "MAROCCO", "MAR"),
        ("100000772", "MARSHALL", "MHL"),
        ("100000773", "MARTINICA", "MTQ"),
        ("100000437", "MAURITANIA", "MRT"),
        ("100000438", "MAURIZIO", "MUS"),
        ("100000774", "MAYOTTE", "MYT"),
        ("100000527", "MESSICO", "MEX"),
        ("100000775", "MICRONESIA STATI FEDERALI", "FSM"),
        ("100000254", "MOLDAVIA", "MDA"),
        ("100000229", "MONACO", "MCO"),
        ("100000341", "MONGOLIA", "MNG"),
        ("100001001", "MONTENEGRO", "MNE"),
        ("100000777", "MONTSERRAT", "MSR"),
        ("100000440", "MOZAMBICO", "MOZ"),
        ("100000307", "MYANMAR-BIRMANIA", "MMR"),
        ("100000441", "NAMIBIA", "NAM"),
        ("100000715", "NAURU", "NRU"),
        ("100000342", "NEPAL", "NPL"),
        ("100000529", "NICARAGUA", "NIC"),
        ("100000442", "NIGER", "NER"),
        ("100000443", "NIGERIA", "NGA"),
        ("100000778", "NORFOLK", "NFK"),
        ("100000231", "NORVEGIA", "NOR"),
        ("100000780", "NUOVA CALEDONIA", "NCL"),
        ("100000719", "NUOVA ZELANDA", "NZL"),
        ("100000343", "OMAN", "OMN"),
        ("100000232", "PAESI BASSI", "NLD"),
        ("100000344", "PAKISTAN", "PAK"),
        ("100000783", "PALAU REPUBBLICA", "PLW"),
        ("110000001", "PALESTINA", "PSE"),
        ("100000530", "PANAMA", "PAN"),
        ("100000721", "PAPUASIA-N.GUINEA", "PNG"),
        ("100000614", "PARAGUAY", "PRY"),
        ("100000615", "PERU'", "PER"),
        ("100000786", "PITCAIRN", "PCN"),
        ("100000787", "POLINESIA", "PYF"),
        ("100000233", "POLONIA", "POL"),
        ("100000234", "PORTOGALLO", "PRT"),
        ("100000790", "PUERTO RICO", "PRI"),
        ("100000345", "QATAR", "QAT"),
        ("100000219", "REGNO UNITO", "GBR"),
        ("100000257", "REPUBBLICA CECA", "CZE"),
        ("100000414", "REPUBBLICA CENTRAFRICANA", "CAF"),
        ("100000998", "REPUBBLICA DEMOCRATICA DEL CONGO", "COD"),
        ("100000516", "REPUBBLICA DOMINICANA", "DOM"),
        ("100000255", "REPUBBLICA SLOVACCA", "SVK"),
        ("100000235", "ROMANIA", "ROU"),
        ("100000446", "RUANDA", "RWA"),
        ("100000534", "S. CHRISTOPHER E NEVIS", "KNA"),
        ("100000533", "S. VINCENT E GRENADINE", "VCT"),
        ("100000795", "SAHARA SPAGNOLO", "ESH"),
        ("100000532", "SAINT LUCIA", "LCA"),
        ("100000796", "SAINT PIERRE ET MIQUELON", "SPM"),
        ("100000797", "SAINT VINCENT E GRENADINE", "VCT"),
        ("100000725", "SALOMONE", "SLB"),
        ("100000727", "SAMOA", "WSM"),
        ("100000798", "SAMOA AMERICANE", "ASM"),
        ("100000236", "SAN MARINO", "SMR"),
        ("100000799", "SANT ELENA", "SHN"),
        ("100000448", "SAO TOME' E PRINCIPE", "STP"),
        ("100000450", "SENEGAL", "SEN"),
        ("100001000", "SERBIA", "SRB"),
        ("100000449", "SEYCHELLES", "SYC"),
        ("100000451", "SIERRA LEONE", "SLE"),
        ("100000346", "SINGAPORE", "SGP"),
        ("100000348", "SIRIA", "SYR"),
        ("100000251", "SLOVENIA", "SVN"),
        ("100000453", "SOMALIA", "SOM"),
        ("100000239", "SPAGNA", "ESP"),
        ("100000311", "SRI LANKA (CEYLON)", "LKA"),
        ("100000536", "STATI UNITI D'AMERICA", "USA"),
        ("100000246", "STATO DELLA CITTA' DEL VATICANO", "VAT"),
        ("100000467", "SUD SUDAN", "SSD"),
        ("100000454", "SUDAFRICA", "ZAF"),
        ("100000455", "SUDAN", "SDN"),
        ("100000616", "SURINAME", "SUR"),
        ("100000240", "SVEZIA", "SWE"),
        ("100000241", "SVIZZERA", "CHE"),
        ("100000456", "SWAZILAND", "SWZ"),
        ("100000362", "TAGIKISTAN", "TJK"),
        ("100000363", "TAIWAN", "TWN"),
        ("100000457", "TANZANIA", "TZA"),
        ("100000349", "THAILANDIA", "THA"),
        ("100000805", "TIMOR", "TLS"),
        ("100000458", "TOGO", "TGO"),
        ("100000806", "TOKELAU", "TKL"),
        ("100000730", "TONGA", "TON"),
        ("100000617", "TRINIDAD E TOBAGO", "TTO"),
        ("100000460", "TUNISIA", "TUN"),
        ("100000351", "TURCHIA", "TUR"),
        ("100000364", "TURKMENISTAN", "TKM"),
        ("100000810", "TURKS", "TCA"),
        ("100000731", "TUVALU", "TUV"),
        ("100000243", "UCRAINA", "UKR"),
        ("100000461", "UGANDA", "UGA"),
        ("100000244", "UNGHERIA", "HUN"),
        ("100000618", "URUGUAY", "URY"),
        ("100000357", "UZBEKISTAN", "UZB"),
        ("100000732", "VANUATU", "VUT"),
        ("100000619", "VENEZUELA", "VEN"),
        ("100000812", "VERGINI BRITANNICHE (ISOLE)", "VGB"),
        ("100000353", "VIETNAM", "VNM"),
        ("100000815", "WALLIS", "WLF"),
        ("100000354", "YEMEN", "YEM"),
        ("100000464", "ZAMBIA", "ZMB"),
        ("100000465", "ZIMBABWE", "ZWE"),
    ];

    private static readonly Dictionary<string, string> ByCode = Rows.ToDictionary(r => r.Code, r => r.Alpha3, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ByName = Rows
        .GroupBy(r => Normalize(r.Description), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First().Alpha3, StringComparer.Ordinal);

    private static readonly HashSet<string> Alpha3Codes = Rows.Select(r => r.Alpha3).ToHashSet(StringComparer.Ordinal);

    /// <summary>The number of countries of the table (for the tests).</summary>
    public static int Count => Rows.Length;

    /// <summary>
    /// The alpha-3 code of a country of the check-in: by its Alloggiati code first, then by its label (the Alloggiati name, or an alpha-3
    /// code typed as such); <c>null</c> when neither is known.
    /// </summary>
    public static string? FromCheckIn(string? alloggiatiCode, string? label)
    {
        if (!string.IsNullOrWhiteSpace(alloggiatiCode) && ByCode.TryGetValue(alloggiatiCode.Trim(), out var byCode))
            return byCode;

        var name = Normalize(label);
        if (name.Length == 0)
            return null;
        if (ByName.TryGetValue(name, out var byName))
            return byName;
        return name.Length == 3 && Alpha3Codes.Contains(name) ? name : null;
    }

    /// <summary>Uppercase, no accents, letters and digits only (the normalization of the Alloggiati descriptions).</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            var upper = char.ToUpperInvariant(c);
            if (upper is >= 'A' and <= 'Z' or >= '0' and <= '9')
                builder.Append(upper);
        }

        return builder.ToString();
    }
}
