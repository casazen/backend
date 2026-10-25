using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases.Reading;

/// <summary>What the receipt of the Agenzia delle Entrate says happened to the contract.</summary>
public enum ReceiptOutcome
{
    /// <summary>The text is not (or not clearly) a receipt of the Agenzia.</summary>
    Unknown = 0,

    /// <summary>The contract was registered ("il contratto è stato registrato", file "acquisito").</summary>
    Registered = 1,

    /// <summary>The Agenzia discarded the request ("scartati: 1", file "scartato"): the contract is not registered.</summary>
    Rejected = 2,
}

/// <summary>
/// What <see cref="RegistrationReceiptReader"/> read in the receipt of the registration (CF-DOCS-REAL, modello RLI12 of the Agenzia delle
/// Entrate): the identification code, the number and series, the day of the registration, the office, the figures of the contract as the
/// Agenzia recorded them and the municipality of the house. It holds no name, no fiscal code and no street: the people and the address of
/// the house are not read. The values are given to the person to check; nothing is stored with the document but the names of the fields.
/// </summary>
public sealed record RegistrationReceiptReading
{
    public ReceiptOutcome Outcome { get; init; }

    /// <summary>"Codice identificativo del contratto" (17 characters, for example <c>TKM26T012345000AB</c>).</summary>
    public string? Code { get; init; }

    public string? Number { get; init; }

    public string? Series { get; init; }

    public DateOnly? RegistrationDate { get; init; }

    public string? Office { get; init; }

    /// <summary>"Importo del canone": the rent of the year, in euros.</summary>
    public decimal? AnnualRent { get; init; }

    public DateOnly? Start { get; init; }

    public DateOnly? End { get; init; }

    public DateOnly? StipulationDate { get; init; }

    /// <summary>"Locazione agevolata immobile uso abitativo", as written.</summary>
    public string? Typology { get; init; }

    /// <summary>The type of contract the typology says, when it says one.</summary>
    public LeaseContractType? TypeHint { get; init; }

    /// <summary>"Risulta contestualmente esercitata l'opzione per la cedolare secca": true when it says so, false when a registered receipt does not.</summary>
    public bool? CedolareSecca { get; init; }

    public string? Comune { get; init; }

    public string? Province { get; init; }

    /// <summary>The receipt says what it must: it is registered with a code or a number, or it is discarded.</summary>
    public bool IsReceipt => Outcome != ReceiptOutcome.Unknown;

    /// <summary>The names of the things that were read, never a value: what is kept with the document.</summary>
    public IReadOnlyList<string> FieldNames()
    {
        var names = new List<string>();
        void Add(string name, bool read)
        {
            if (read)
                names.Add(name);
        }

        Add("code", Code is not null);
        Add("number", Number is not null);
        Add("date", RegistrationDate is not null);
        Add("office", Office is not null);
        Add("rent", AnnualRent is not null);
        Add("period", Start is not null || End is not null);
        Add("stipulation", StipulationDate is not null);
        Add("typology", Typology is not null);
        Add("cedolareSecca", CedolareSecca is not null);
        Add("comune", Comune is not null);
        return names;
    }
}

/// <summary>
/// Reads the receipt that the Agenzia delle Entrate returns when a lease is registered (CF-DOCS-REAL): "Comunicazione di avvenuto
/// ricevimento" and "Ricevuta di avvenuta registrazione contratti di locazione (mod. RLI12)", a digital PDF of three pages — the file
/// acquired and how many contracts were registered or discarded, the receipt itself with the office, the code, the figures and the house,
/// and the list of the documents acquired or discarded. Pure function of the text (no OCR is needed): a value is read only next to the
/// words that say what it is; a receipt of a discarded request is said to be one, not read as a registration.
/// </summary>
public static partial class RegistrationReceiptReader
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;

    [GeneratedRegex(@"\bregistrati\s*:\s*(?<n>\d+)", Options, Timeout)]
    private static partial Regex RegisteredCount();

    [GeneratedRegex(@"\bscartati\s*:\s*(?<n>\d+)", Options, Timeout)]
    private static partial Regex RejectedCount();

    [GeneratedRegex(@"\bricevuta\s+di\s+avvenuta\s+registrazione\b", Options, Timeout)]
    private static partial Regex ReceiptTitle();

    // "Il contratto e' stato registrato il 21/09/2026 al n. 012345 serie 3T e codice identificativo TKM26T012345000AB"
    [GeneratedRegex(@"\bcontratto\s+\p{L}'?\s+stato\s+registrato\s+il\s+(?<d>" + ReadingText.DatePattern + @")", Options, Timeout)]
    private static partial Regex RegisteredSentence();

    [GeneratedRegex(
        @"\bregistrato\s+il\s+" + ReadingText.DatePattern + @"\s+al\s+n\W{0,2}\s*(?<n>[A-Za-z0-9]{1,12})\s+serie\s+(?<s>[A-Za-z0-9]{1,4})\b",
        Options,
        Timeout)]
    private static partial Regex NumberAndSeries();

    [GeneratedRegex(@"\bcodice\s+identificativo(?:\s+del\s+contratto)?\s*:?\s*(?<c>[A-Za-z0-9]{10,25})\b", Options, Timeout)]
    private static partial Regex IdentificationCode();

    [GeneratedRegex(@"\bufficio\s+territoriale\s+di\s+(?<o>.{2,40}?)\s+(?=codice\s+identificativo)", Options, Timeout)]
    private static partial Regex OfficeText();

    [GeneratedRegex(@"\bdurata\s+dal\s+(?<a>" + ReadingText.DatePattern + @")\s+al\s+(?<b>" + ReadingText.DatePattern + @")", Options, Timeout)]
    private static partial Regex DurationText();

    [GeneratedRegex(@"\bdata\s+di\s+stipula\s+(?<d>" + ReadingText.DatePattern + @")", Options, Timeout)]
    private static partial Regex StipulationText();

    [GeneratedRegex(@"\bimporto\s+del\s+canone\s+(?<n>" + ReadingText.AmountPattern + @")", Options, Timeout)]
    private static partial Regex RentText();

    [GeneratedRegex(
        @"\btipologia\s*:\s*(?<t>.{3,80}?)(?=\s+\p{L}'?\s+presente\b|\s+soggetti\s+destinatari\b|\s+contratto\s+esente\b|\s+dati\s+degli\s+immobili\b|\s*$)",
        Options,
        Timeout)]
    private static partial Regex TypologyText();

    [GeneratedRegex(@"\besercitata\b.{0,40}?\bcedolare\s+secca\b", Options, Timeout)]
    private static partial Regex CedolareExercised();

    [GeneratedRegex(@"\bubicato\s+nel\s+comune\s+di\s+(?<c>.{2,40}?)\s+prov\W{0,2}\s*(?<p>[A-Za-z]{2})\b", Options, Timeout)]
    private static partial Regex ComuneText();

    [GeneratedRegex(@"\bdenominazione\s+(?<e>acquisit\w+|scartat\w+)", Options, Timeout)]
    private static partial Regex ListedOutcome();

    /// <summary>Reads the receipt from the text of the PDF (empty or not a receipt: <see cref="RegistrationReceiptReading.Outcome"/> is <see cref="ReceiptOutcome.Unknown"/>).</summary>
    public static RegistrationReceiptReading Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RegistrationReceiptReading();

        var flat = LeaseContractTextParser.Flatten(text);
        var sentence = RegisteredSentence().Match(flat);
        var titled = ReceiptTitle().IsMatch(flat);
        var registered = CountOf(RegisteredCount(), flat);
        var rejected = CountOf(RejectedCount(), flat);
        var listed = ListedOutcome().Match(flat);
        var listedRejected = listed.Success && listed.Groups["e"].Value.StartsWith("scart", StringComparison.OrdinalIgnoreCase);

        var outcome = (sentence.Success || titled) && !listedRejected && !(rejected > 0 && registered == 0)
            ? ReceiptOutcome.Registered
            : rejected > 0 || listedRejected
                ? ReceiptOutcome.Rejected
                : ReceiptOutcome.Unknown;
        if (outcome == ReceiptOutcome.Unknown)
            return new RegistrationReceiptReading();

        var number = NumberAndSeries().Match(flat);
        var duration = DurationText().Match(flat);
        DateOnly? start = null;
        DateOnly? end = null;
        if (duration.Success && ReadingText.Date(duration.Groups["a"].Value) is { } first && ReadingText.Date(duration.Groups["b"].Value) is { } last && last > first)
            (start, end) = (first, last);

        var typology = TypologyText().Match(flat) is { Success: true } kind ? Clean(kind.Groups["t"].Value) : null;
        var comune = ComuneText().Match(flat);
        return new RegistrationReceiptReading
        {
            Outcome = outcome,
            Code = CodeOf(flat),
            Number = number.Success ? number.Groups["n"].Value : null,
            Series = number.Success ? number.Groups["s"].Value : null,
            RegistrationDate = sentence.Success ? ReadingText.Date(sentence.Groups["d"].Value) : null,
            Office = OfficeText().Match(flat) is { Success: true } office ? Clean(office.Groups["o"].Value) : null,
            AnnualRent = RentText().Match(flat) is { Success: true } rent ? ReadingText.Amount(rent.Groups["n"].Value) : null,
            Start = start,
            End = end,
            StipulationDate = StipulationText().Match(flat) is { Success: true } stipula ? ReadingText.Date(stipula.Groups["d"].Value) : null,
            Typology = typology,
            TypeHint = TypeOf(typology),
            CedolareSecca = outcome == ReceiptOutcome.Registered ? CedolareExercised().IsMatch(flat) : null,
            Comune = comune.Success ? Clean(comune.Groups["c"].Value) : null,
            Province = comune.Success ? comune.Groups["p"].Value.ToUpperInvariant() : null,
        };
    }

    private static int CountOf(Regex regex, string flat) =>
        regex.Match(flat) is { Success: true } match && int.TryParse(match.Groups["n"].Value, out var count) ? count : 0;

    /// <summary>The first identification code that looks like one: letters and digits, with both (a word that follows an empty field is not).</summary>
    private static string? CodeOf(string flat)
    {
        foreach (Match match in IdentificationCode().Matches(flat))
        {
            var code = match.Groups["c"].Value.ToUpperInvariant();
            if (code.Any(char.IsDigit) && code.Any(char.IsLetter))
                return code;
        }

        return null;
    }

    private static string Clean(string text) => text.Trim(' ', '.', ',', ':', '-');

    /// <summary>"Locazione agevolata…" is a canone concordato, "…transitoria" a transitory lease, any other "locazione… uso abitativo" a free one.</summary>
    private static LeaseContractType? TypeOf(string? typology)
    {
        if (typology is null)
            return null;

        var words = ReadingText.Normalize(typology);
        if (words.Contains("agevolat", StringComparison.Ordinal) || words.Contains("concordat", StringComparison.Ordinal) || words.Contains("convenzionat", StringComparison.Ordinal))
            return LeaseContractType.Concordato;
        if (words.Contains("transitori", StringComparison.Ordinal))
            return LeaseContractType.Transitorio;
        return words.Contains("abitativ", StringComparison.Ordinal) ? LeaseContractType.Libero : null;
    }
}

/// <summary>The stable codes of the ways a receipt can disagree with the contract it is about (the screen puts each in words).</summary>
public static class RegistrationReceiptWarnings
{
    public const string RentDiffers = "receipt_rent_differs";
    public const string StartDiffers = "receipt_start_differs";
    public const string EndDiffers = "receipt_end_differs";
    public const string StipulationDiffers = "receipt_stipulation_differs";
    public const string ComuneDiffers = "receipt_comune_differs";
    public const string TypeDiffers = "receipt_type_differs";
    public const string RegimeDiffers = "receipt_regime_differs";
}

/// <summary>What the contract of CasaZen says, to be compared with its receipt: the figures of the lease and the town of its house.</summary>
public sealed record ReceiptLeaseFacts(
    decimal MonthlyRent,
    DateOnly Start,
    DateOnly End,
    DateOnly? Stipulation,
    LeaseContractType ContractType,
    LeaseTaxRegime? TaxRegime,
    string? City);

/// <summary>
/// Compares a receipt with its contract (CF-DOCS-REAL). A disagreement is a warning to the person, never a block: the receipt is the
/// Agenzia's word, and the person declares the registration after checking. Only what the receipt states is compared.
/// </summary>
public static class RegistrationReceiptCheck
{
    /// <summary>The year's rent of the receipt is the month's times twelve, up to the cents a division leaves and the rounding of an office.</summary>
    private const decimal RentTolerance = 1m;

    public static IReadOnlyList<string> Compare(RegistrationReceiptReading receipt, ReceiptLeaseFacts lease)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(lease);

        var warnings = new List<string>();
        if (receipt.Outcome != ReceiptOutcome.Registered)
            return warnings;

        if (receipt.AnnualRent is { } rent && Math.Abs(rent - (lease.MonthlyRent * 12m)) > RentTolerance)
            warnings.Add(RegistrationReceiptWarnings.RentDiffers);
        if (receipt.Start is { } start && start != lease.Start)
            warnings.Add(RegistrationReceiptWarnings.StartDiffers);
        if (receipt.End is { } end && end != lease.End)
            warnings.Add(RegistrationReceiptWarnings.EndDiffers);
        if (receipt.StipulationDate is { } stipula && lease.Stipulation is { } signed && stipula != signed)
            warnings.Add(RegistrationReceiptWarnings.StipulationDiffers);
        if (receipt.Comune is { } comune && lease.City is { Length: > 0 } city && !SameTown(comune, city))
            warnings.Add(RegistrationReceiptWarnings.ComuneDiffers);
        if (receipt.TypeHint is { } type && type != lease.ContractType)
            warnings.Add(RegistrationReceiptWarnings.TypeDiffers);
        if (receipt.CedolareSecca is { } cedolare && lease.TaxRegime is { } regime && cedolare != (regime == LeaseTaxRegime.CedolareSecca))
            warnings.Add(RegistrationReceiptWarnings.RegimeDiffers);

        return warnings;
    }

    /// <summary>The same town, written in capitals or not, with or without its province or an accent.</summary>
    internal static bool SameTown(string a, string b)
    {
        var first = ReadingText.Normalize(a);
        var second = ReadingText.Normalize(b);
        return first.Length > 0 && second.Length > 0
            && (first == second || first.StartsWith(second + " ", StringComparison.Ordinal) || second.StartsWith(first + " ", StringComparison.Ordinal));
    }
}
