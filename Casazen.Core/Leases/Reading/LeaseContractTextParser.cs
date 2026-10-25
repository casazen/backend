using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases.Reading;

/// <summary>
/// Reads the data of an Italian residential lease contract from its text (NL-DOCS): the people with their fiscal code and residence, the
/// type, the tax regime, the rent, the deposit, the dates and the day of the signature — what the wizard of an imported contract asks in
/// steps 2 to 4 and when the contract was signed. A pure function of the text (digital or read by OCR) and, to compare the address,
/// of the property chosen: no I/O, nothing logged.
/// </summary>
/// <remarks>
/// <para><b>It does not invent.</b> A value is returned only when the text states it next to a word that says what it is ("canone",
/// "decorrenza", "deposito cauzionale"…), and each carries a confidence: <see cref="AcceptedConfidence"/> and above is what a draft
/// takes. A value the contract states twice in different ways (two monthly rents, two types) is not returned: a mistake in a rent or a
/// fiscal code costs more than a field the person types. A person is read only with a fiscal code that passed the check character.</para>
/// <para>The text is first flattened (one line, one space between words), because a contract wraps its sentences wherever the page
/// ends; every rule looks for a label and the value that follows it within a few words.</para>
/// </remarks>
public static partial class LeaseContractTextParser
{
    /// <summary>The confidence from which a read value goes into a draft (the person still checks it).</summary>
    public const int AcceptedConfidence = 70;

    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;

    /// <summary>Reads <paramref name="text"/>; <paramref name="context"/> is the property of the draft, to say whether the contract names it.</summary>
    public static LeaseContractReading Parse(string? text, LeaseContractParseContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return LeaseContractReading.Empty;

        var flat = Flatten(text);
        var dates = FindDates(flat);

        var months = ReadTermMonths(flat);
        var period = ReadPeriod(flat, dates);
        var type = ReadContractType(flat, months?.Value);
        var (rent, deposit) = ReadMoney(flat);

        ReadValue<DateOnly>? end = period.End;
        if (end is null && period.Start is { } start && months is { } length)
        {
            // The contract says how long it lasts and when it starts, not when it ends: worked out, and told so.
            var worked = start.Value.AddMonths(length.Value).AddDays(-1);
            end = new ReadValue<DateOnly>(worked, Math.Min(start.Confidence, length.Confidence) - 10, Derived: true);
        }

        return new LeaseContractReading
        {
            Parties = ReadParties(flat),
            ContractType = type,
            TaxRegime = ReadTaxRegime(flat),
            MonthlyRent = rent,
            SecurityDeposit = deposit,
            StartDate = period.Start,
            EndDate = end,
            TermMonths = months,
            StipulaDate = ReadStipulaDate(flat, dates),
            Property = ReadProperty(flat, context),
        };
    }

    // ---- the text --------------------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\.{3,}|\u2026+|-{3,}|={3,}|\*{3,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex Placeholders();

    [GeneratedRegex(@" {2,}", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex ManySpaces();

    /// <summary>One line: a single space between words, the quotes and dashes of the typography brought to the plain ones, the blanks of a form (dots, underscores) gone.</summary>
    internal static string Flatten(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var raw in text)
        {
            if (raw is '\u00ad' or '\u200b')
                continue;

            var c = raw switch
            {
                '\u2019' or '\u2018' or '`' or '\u00b4' => '\'',
                '\u201c' or '\u201d' or '\u00ab' or '\u00bb' or '\u201e' => '"',
                '\u2013' or '\u2014' or '\u2212' or '\u2010' or '\u2011' => '-',
                '|' or '_' => ' ',
                _ => raw,
            };
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                c = ' ';

            if (c == ' ')
            {
                if (!lastWasSpace)
                    builder.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                builder.Append(c);
                lastWasSpace = false;
            }
        }

        return ManySpaces().Replace(Placeholders().Replace(builder.ToString(), " "), " ").Trim();
    }

    // ---- dates -----------------------------------------------------------------------------------------------------------

    private readonly record struct FoundDate(DateOnly Date, int Index, int End);

    [GeneratedRegex(@"(?<![\d/.\-])(?<d>\d{1,2}) ?[/.\-] ?(?<m>\d{1,2}) ?[/.\-] ?(?<y>\d{4})(?!\d)", Options, Timeout)]
    private static partial Regex NumericDate();

    [GeneratedRegex(
        @"(?<!\d)(?<d>\d{1,2}|primo) ?[\u00b0\u00ba]? ?(?<m>gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre)\.?,? ?(?<y>\d{4})(?!\d)",
        Options,
        Timeout)]
    private static partial Regex WrittenDate();

    private static readonly string[] Months =
        ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"];

    /// <summary>Every real calendar date written in the text (numeric or in words), between 1970 and 2100, in the order they appear.</summary>
    private static List<FoundDate> FindDates(string flat)
    {
        var dates = new List<FoundDate>();

        void Add(Match match, int day, int month, int year)
        {
            if (year < LeaseDraftRules.MinDate.Year || year > LeaseDraftRules.MaxDate.Year || month is < 1 or > 12
                || day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                return;
            }

            dates.Add(new FoundDate(new DateOnly(year, month, day), match.Index, match.Index + match.Length));
        }

        foreach (Match match in NumericDate().Matches(flat))
        {
            Add(match, int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        foreach (Match match in WrittenDate().Matches(flat))
        {
            var day = match.Groups["d"].Value.Equals("primo", StringComparison.OrdinalIgnoreCase)
                ? 1
                : int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
            var month = Array.IndexOf(Months, match.Groups["m"].Value.ToLowerInvariant()) + 1;
            Add(match, day, month, int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        return dates.OrderBy(d => d.Index).ToList();
    }

    /// <summary>The words that may stand between a label ("decorrenza") and its date ("dal giorno"): nothing else, or the date is another one's.</summary>
    private static readonly HashSet<string> GapWords =
        ["dal", "dall", "del", "dello", "della", "dalla", "giorno", "il", "la", "lo", "l", "al", "data", "a", "far", "partire", "di", "sino", "fino", "entro", "in", "odierna"];

    [GeneratedRegex(@"[^\p{L}]+", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex NotLetters();

    private static bool IsStrictGap(string gap)
    {
        var words = NotLetters().Split(gap).Where(w => w.Length > 0).ToList();
        return words.Count <= 5 && words.All(w => GapWords.Contains(w.ToLowerInvariant()));
    }

    /// <summary>
    /// The first date that follows a match of <paramref name="label"/> with only prepositions in between ("decorrenza dal 01/11/2026");
    /// with <paramref name="loose"/>, up to <paramref name="maxGap"/> characters of anything without a digit ("sottoscritto. Milano, li 15/10/2026").
    /// The first label that has one wins, or the last when <paramref name="last"/> (the signature is at the end).
    /// </summary>
    private static FoundDate? DateAfter(
        string flat, IReadOnlyList<FoundDate> dates, Regex label, bool last = false, bool loose = false, int maxGap = 45, Func<string, int, bool>? skip = null)
    {
        FoundDate? chosen = null;
        foreach (Match match in label.Matches(flat))
        {
            // A label that is not the one looked for ("mediante passaporto … con scadenza 03/03/2030" is not the end of the lease).
            if (skip is not null && skip(flat, match.Index))
                continue;

            var from = match.Index + match.Length;
            foreach (var date in dates)
            {
                if (date.Index < from)
                    continue;

                var gap = flat[from..date.Index];
                var fits = loose ? gap.Length <= maxGap && !gap.Any(char.IsDigit) : gap.Length <= 40 && IsStrictGap(gap);
                if (fits)
                {
                    if (!last)
                        return date;
                    chosen = date;
                }

                break;
            }
        }

        return chosen;
    }

    // The documents a contract describes and that expire: the identity document of the tenant, the energy certificate of the house.
    [GeneratedRegex(
        @"\b(?:passaporto|permesso\s+di\s+soggiorno|carta\s+d.\s?identit\w*|documento\s+(?:di\s+)?(?:identit\w*|riconoscimento)|patente|prestazione\s+energetica|attestato\s+di\s+prestazione|certificat\w+\s+energetic\w*|a\.?\s?p\.?\s?e\.?)\b",
        Options,
        Timeout)]
    private static partial Regex DocumentWord();

    /// <summary>Whether the label at <paramref name="index"/> closes the description of a document ("…con scadenza 03/03/2030"), looking back at the words just before it.</summary>
    private static bool ExpiresADocument(string flat, int index)
    {
        var before = flat[Math.Max(0, index - 170)..index];
        return DocumentWord().IsMatch(before);
    }

    // ---- the first period: start, end, length -----------------------------------------------------------------------------

    [GeneratedRegex(
        @"\b(?:decorrenz[ae]|decorr(?:e|ere|er[\u00e0a]|ono|endo)|ha\s+inizio|avr[\u00e0a]\s+inizio|data\s+di\s+inizio|inizio|a\s+partire|a\s+far\s+data|con\s+effetto|entrer[\u00e0a]\s+in\s+vigore)\b",
        Options,
        Timeout)]
    private static partial Regex StartLabel();

    [GeneratedRegex(
        @"\b(?:scadenz[ae]|scade|scadr[\u00e0a]|termin[ae]|avr[\u00e0a]\s+termine|ha\s+termine|cessa(?:r[\u00e0a])?|fino|sino|data\s+di\s+(?:fine|cessazione))\b",
        Options,
        Timeout)]
    private static partial Regex EndLabel();

    [GeneratedRegex(@"\bdal(?:\s+giorno)?\s*$", Options, Timeout)]
    private static partial Regex FromBefore();

    [GeneratedRegex(@"^\s*(?:e\s+)?(?:fino\s+|sino\s+)?al(?:\s+giorno)?\s*$", Options, Timeout)]
    private static partial Regex ToBetween();

    private sealed record Period(ReadValue<DateOnly>? Start, ReadValue<DateOnly>? End);

    private static Period ReadPeriod(string flat, IReadOnlyList<FoundDate> dates)
    {
        var start = DateAfter(flat, dates, StartLabel());
        var end = DateAfter(flat, dates, EndLabel(), skip: ExpiresADocument);
        var startConfidence = 92;
        var endConfidence = 92;

        // "dal 01/11/2026 al 31/10/2030": two dates joined by "al", the first after a "dal".
        for (var i = 0; i + 1 < dates.Count; i++)
        {
            var first = dates[i];
            var second = dates[i + 1];
            if (second.Index < first.End)
                continue;

            var before = flat[Math.Max(0, first.Index - 16)..first.Index];
            if (!FromBefore().IsMatch(before) || !ToBetween().IsMatch(flat[first.End..second.Index]))
                continue;

            if (start is null)
            {
                start = first;
                startConfidence = 85;
            }

            if (end is null)
            {
                end = second;
                endConfidence = 85;
            }

            break;
        }

        // An end on or before the start is a misreading of one of them: neither is trusted as a pair, the start alone stays.
        if (start is { } s && end is { } e && e.Date <= s.Date)
            end = null;

        return new Period(
            start is { } a ? new ReadValue<DateOnly>(a.Date, startConfidence) : null,
            end is { } b ? new ReadValue<DateOnly>(b.Date, endConfidence) : null);
    }

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["un"] = 1,
        ["uno"] = 1,
        ["una"] = 1,
        ["due"] = 2,
        ["tre"] = 3,
        ["quattro"] = 4,
        ["cinque"] = 5,
        ["sei"] = 6,
        ["sette"] = 7,
        ["otto"] = 8,
        ["nove"] = 9,
        ["dieci"] = 10,
        ["undici"] = 11,
        ["dodici"] = 12,
        ["tredici"] = 13,
        ["quattordici"] = 14,
        ["quindici"] = 15,
        ["sedici"] = 16,
        ["diciassette"] = 17,
        ["diciotto"] = 18,
        ["ventiquattro"] = 24,
        ["trentasei"] = 36,
        ["quarantotto"] = 48,
    };

    private const string NumberPattern = @"\d{1,3}|un|uno|una|due|tre|quattro|cinque|sei|sette|otto|nove|dieci|undici|dodici|tredici|quattordici|quindici|sedici|diciassette|diciotto|ventiquattro|trentasei|quarantotto";

    [GeneratedRegex(@"\bdurata\b", Options, Timeout)]
    private static partial Regex DurationLabel();

    // "4 (quattro) anni", "quattro anni", "36 mesi"
    [GeneratedRegex(@"(?<![\p{L}\d/])(?<n>" + NumberPattern + @")\s*(?:\([^)]{0,30}\)\s*)?(?<u>anni|anno|mesi|mese)\b", Options, Timeout)]
    private static partial Regex NumberThenUnit();

    // "anni 4", "mesi 12"
    [GeneratedRegex(@"\b(?<u>anni|anno|mesi|mese)\s*(?<n>" + NumberPattern + @")\b(?![\d/])", Options, Timeout)]
    private static partial Regex UnitThenNumber();

    /// <summary>How long the first period lasts, in months, from the first "durata" followed within a few words by a number of years or months.</summary>
    private static ReadValue<int>? ReadTermMonths(string flat)
    {
        foreach (Match label in DurationLabel().Matches(flat))
        {
            var from = label.Index + label.Length;
            var segment = flat.Substring(from, Math.Min(80, flat.Length - from));
            var found = FirstBy(NumberThenUnit().Matches(segment)) ?? FirstBy(UnitThenNumber().Matches(segment));
            if (found is not { } match)
                continue;

            var number = int.TryParse(match.Groups["n"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var digits)
                ? digits
                : NumberWords.GetValueOrDefault(match.Groups["n"].Value);
            var months = match.Groups["u"].Value.StartsWith("ann", StringComparison.OrdinalIgnoreCase) ? number * 12 : number;
            if (months is >= 1 and <= 120)
                return new ReadValue<int>(months, 88);
        }

        return null;
    }

    private static Match? FirstBy(MatchCollection matches) => matches.Count > 0 ? matches[0] : null;

    // ---- the day of the signature ------------------------------------------------------------------------------------------

    [GeneratedRegex(@"\bdata\s+di\s+(?:stipula|stipulazione|sottoscrizione)\b|\b(?:stipulat[oa]|concluso|redatto)\s+(?:in\s+data|il)\b", Options, Timeout)]
    private static partial Regex StipulaLabel();

    [GeneratedRegex(@"\b(?:letto,?\s+(?:confermato|approvato)\s+e\s+sottoscritto|sottoscritto\s+(?:in|a)\s+\p{L}[\p{L}' ]{1,30}\s+il|luogo\s+e\s+data)", Options, Timeout)]
    private static partial Regex SignatureLabel();

    [GeneratedRegex(@"\b(?:li|l\u00ec|addi|add\u00ec)\b[,.:]?\s*$", Options, Timeout)]
    private static partial Regex LiBefore();

    [GeneratedRegex(@"\baccordo\b[^.;]{0,140}$", Options, Timeout)]
    private static partial Regex AgreementBefore();

    private static bool IsAgreementDate(string flat, int index) => AgreementBefore().IsMatch(flat[Math.Max(0, index - 160)..index]);

    [GeneratedRegex(
        @"\bletto,?\s+(?:confermato|approvato)\s+e\s+sottoscritto\b.{0,70}?\bli\W{0,3}(?<d>\d{1,2})\s?[/.\-]\s?(?<m>\d{1,2})\s?[/.\-]\s?(?<y>\d{2})(?![\d/.\-])",
        Options,
        Timeout)]
    private static partial Regex SignedShortDate();

    private static ReadValue<DateOnly>? ReadStipulaDate(string flat, IReadOnlyList<FoundDate> dates)
    {
        if (DateAfter(flat, dates, StipulaLabel(), last: true) is { } labelled)
            return new ReadValue<DateOnly>(labelled.Date, 88);

        // The signature block at the end: "Letto, confermato e sottoscritto. Milano, li 15/10/2026" ("l'Accordo … sottoscritto in Milano il 20 maggio 2025" is not it).
        if (DateAfter(flat, dates, SignatureLabel(), last: true, loose: true, maxGap: 70, skip: IsAgreementDate) is { } signature)
            return new ReadValue<DateOnly>(signature.Date, 80);

        // "Letto, approvato e sottoscritto. Bollate, li 19-9-26": a year of two digits, as the parties write it by hand.
        if (SignedShortDate().Match(flat) is { Success: true } shortDate
            && ReadingText.Date($"{shortDate.Groups["d"].Value}/{shortDate.Groups["m"].Value}/{shortDate.Groups["y"].Value}") is { } shortDay)
        {
            return new ReadValue<DateOnly>(shortDay, 72);
        }

        // "Milano, li 15 ottobre 2026": the last date written after "li" is the signature's.
        FoundDate? afterLi = null;
        foreach (var date in dates)
        {
            if (LiBefore().IsMatch(flat[Math.Max(0, date.Index - 8)..date.Index]))
                afterLi = date;
        }

        return afterLi is { } li ? new ReadValue<DateOnly>(li.Date, 72) : null;
    }
}
