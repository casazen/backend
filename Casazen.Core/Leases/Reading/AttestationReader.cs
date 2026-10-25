using System.Globalization;
using System.Text.RegularExpressions;

namespace Casazen.Core.Leases.Reading;

/// <summary>What an attestation or a calculation sheet says about the rent: it is within the territorial agreement, or it is not.</summary>
public enum ConformityVerdict
{
    /// <summary>The text does not say, or says both.</summary>
    Unknown = 0,

    /// <summary>The rent is within the values of the territorial agreement.</summary>
    Conform = 1,

    /// <summary>The rent is outside the values of the territorial agreement.</summary>
    NotConform = 2,
}

/// <summary>
/// What <see cref="AttestationReader"/> read in the section of the attestation of conformity (CF-DOCS-REAL): the municipality and the
/// territorial agreement, the organizations that attest, whether the rent conforms, the rent they attest and the dates. It is kept only
/// while the document is checked against the contract: nothing of it is stored with the document, which keeps the names of the fields.
/// </summary>
public sealed record AttestationReading
{
    public string? Comune { get; init; }

    public string? Province { get; init; }

    /// <summary>The area the territorial agreement covers ("Garbagnate Milanese"), as written.</summary>
    public string? AgreementArea { get; init; }

    public DateOnly? AgreementDate { get; init; }

    public string? OwnersOrganization { get; init; }

    public string? TenantsOrganization { get; init; }

    public ConformityVerdict Verdict { get; init; }

    /// <summary>The monthly rent the attestation says the parties agreed.</summary>
    public decimal? MonthlyRent { get; init; }

    public decimal? RangeMinimum { get; init; }

    public decimal? RangeMaximum { get; init; }

    public string? Zone { get; init; }

    public int? SubBand { get; init; }

    public decimal? Surface { get; init; }

    public int? Points { get; init; }

    public DateOnly? Start { get; init; }

    public DateOnly? End { get; init; }

    public int? TermMonths { get; init; }

    /// <summary>The day written next to "Luogo e data", when it can be read (it is often handwritten).</summary>
    public DateOnly? SignedOn { get; init; }

    /// <summary>The number of the attestation, when it carries one.</summary>
    public string? Number { get; init; }

    /// <summary>The valid fiscal codes of the people the attestation names, to be compared with the contract's.</summary>
    public IReadOnlyList<string> FiscalCodes { get; init; } = [];

    /// <summary>The names of the things that were read, never a value: what is kept with the document.</summary>
    public IReadOnlyList<string> FieldNames()
    {
        var names = new List<string>();
        void Add(string name, bool read)
        {
            if (read)
                names.Add(name);
        }

        Add("comune", Comune is not null);
        Add("agreement", AgreementArea is not null || AgreementDate is not null);
        Add("organizations", OwnersOrganization is not null || TenantsOrganization is not null);
        Add("verdict", Verdict != ConformityVerdict.Unknown);
        Add("rent", MonthlyRent is not null);
        Add("rentRange", RangeMinimum is not null && RangeMaximum is not null);
        Add("zone", Zone is not null);
        Add("surface", Surface is not null);
        Add("period", Start is not null || End is not null);
        Add("signedOn", SignedOn is not null);
        Add("parties", FiscalCodes.Count > 0);
        return names;
    }
}

/// <summary>
/// Reads the attestation of conformity ("Attestazione di Rispondenza", D.M. 16 gennaio 2017) that the associations of the owners and of
/// the tenants release for a canone concordato contract: the text of the section, as the OCR gave it. A pure function: a value is read
/// only where the text states it next to the words that say what it is, and a field the text does not state is simply not there.
/// </summary>
public static partial class AttestationReader
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;

    // "— Comune di Bollate (MI)"
    [GeneratedRegex(@"\bcomune\s+di\s+(?<c>\p{L}[\p{L}'. \-]{1,38}?)\s*[(\[]\s*(?<p>[A-Za-z]{2})\s*[)\]]", Options, Timeout)]
    private static partial Regex ComuneWithProvince();

    // "rientrano nei valori previsti dall'Accordo territoriale vigente per il Comune di Bollate"
    [GeneratedRegex(@"\b(?i:per\s+il\s+comune\s+di)\s+(?<c>\p{Lu}[\p{L}'\-]+(?: \p{Lu}[\p{L}'\-]+){0,2})", RegexOptions.CultureInvariant, Timeout)]
    private static partial Regex ComuneAfterFor();

    // "Accordo locale per i Comuni dell'ambito di Garbagnate Milanese, sottoscritto in Milano il 20 maggio 2025"
    [GeneratedRegex(
        @"\baccordo\s+(?:locale|territoriale)\s+per\s+i\s+comuni\s+dell\W{0,2}\s*ambito\s+di\s+(?<a>\p{L}[\p{L}'\- ]{1,48}?)\s*,\s*sottoscritto",
        Options,
        Timeout)]
    private static partial Regex AgreementAreaText();

    [GeneratedRegex(
        @"\baccordo\s+(?:locale|territoriale)\b.{0,160}?\bsottoscritto\b.{0,40}?\bil\s+(?<d>\d{1,2}\s*(?:[/.\-]\s*\d{1,2}\s*[/.\-]\s*\d{4}|\s+(?:gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre)\s+\d{4}))",
        Options,
        Timeout)]
    private static partial Regex AgreementDateText();

    [GeneratedRegex(@"\borganizzazione\s+della\s+propriet\p{L}\s+edilizia\s+(?<o>[^,;]{3,90}?)\s*,", Options, Timeout)]
    private static partial Regex OwnersOrganizationText();

    [GeneratedRegex(@"\borganizzazione\s+sindacale\s+dei\s+conduttori\s+(?<o>[^,;]{3,60}?)\s*,", Options, Timeout)]
    private static partial Regex TenantsOrganizationText();

    [GeneratedRegex(@"\battestano\b.{0,900}?\brientrano\s+(?:tutti\s+)?nei\s+valori", Options, Timeout)]
    private static partial Regex AttestsWithin();

    [GeneratedRegex(@"\bnon\s+(?:risultano\s+|sono\s+)?(?:rientran\w+|rispondent\w+|conform\w+)\b", Options, Timeout)]
    private static partial Regex AttestsNot();

    [GeneratedRegex(@"\bzona\s+(?<z>\d{1,2})\b", Options, Timeout)]
    private static partial Regex ZoneText();

    [GeneratedRegex(@"\bsub-?\s?fascia\s+(?<s>\d)\b", Options, Timeout)]
    private static partial Regex SubBandText();

    [GeneratedRegex(@"\bsuperficie\s+convenzionale\s+(?:di\s+)?mq\.?\s*(?<n>\d+(?:[.,]\d+)?)", Options, Timeout)]
    private static partial Regex SurfaceText();

    [GeneratedRegex(@"\bpunteggio\s+totale\W{0,3}(?<n>\d{1,3})\s+punti", Options, Timeout)]
    private static partial Regex PointsText();

    [GeneratedRegex(@"\bdal\s+(?<a>" + ReadingText.DatePattern + @")\s+al\s+(?<b>" + ReadingText.DatePattern + @")", Options, Timeout)]
    private static partial Regex PeriodText();

    [GeneratedRegex(@"(?<![\d/])(?<n>\d{1,3})\s+mesi\s+di\s+durata\s+iniziale", Options, Timeout)]
    private static partial Regex TermText();

    [GeneratedRegex(@"\bluogo\s+e\s+data\W{0,6}(?:\p{L}[\p{L}'\- ]{1,30}?\W{1,6})?(?<d>" + ReadingText.DatePattern + @")", Options, Timeout)]
    private static partial Regex SignedOnText();

    [GeneratedRegex(@"\battestazione\s+(?:numero|n)\b\W{0,3}(?<n>[A-Za-z0-9][A-Za-z0-9\-/]{2,24})", Options, Timeout)]
    private static partial Regex NumberText();

    /// <summary>Reads the attestation from the text of its section (empty or without the attestation: nothing is read, nothing is invented).</summary>
    public static AttestationReading Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new AttestationReading();

        var flat = LeaseContractTextParser.Flatten(text);
        var rent = AgreedRentReader.Read(flat);

        string? comune = null;
        string? province = null;
        if (ComuneWithProvince().Match(flat) is { Success: true } withProvince)
        {
            comune = withProvince.Groups["c"].Value.Trim(' ', '.', '-');
            province = withProvince.Groups["p"].Value.ToUpperInvariant();
        }
        else if (ComuneAfterFor().Match(flat) is { Success: true } after)
        {
            comune = after.Groups["c"].Value.Trim();
        }

        var period = PeriodText().Match(flat);
        DateOnly? start = null;
        DateOnly? end = null;
        if (period.Success && ReadingText.Date(period.Groups["a"].Value) is { } first && ReadingText.Date(period.Groups["b"].Value) is { } last && last > first)
            (start, end) = (first, last);

        var term = TermText().Match(flat);
        return new AttestationReading
        {
            Comune = comune,
            Province = province,
            AgreementArea = AgreementAreaText().Match(flat) is { Success: true } area ? area.Groups["a"].Value.Trim() : null,
            AgreementDate = AgreementDateOf(flat),
            OwnersOrganization = OwnersOrganizationText().Match(flat) is { Success: true } owners ? owners.Groups["o"].Value.Trim() : null,
            TenantsOrganization = TenantsOrganizationText().Match(flat) is { Success: true } tenants ? tenants.Groups["o"].Value.Trim() : null,
            Verdict = VerdictOf(flat),
            MonthlyRent = rent.Monthly ?? (rent.Annual is { } year ? decimal.Round(year / 12m, 2) : null),
            RangeMinimum = rent.Minimum,
            RangeMaximum = rent.Maximum,
            Zone = ZoneText().Match(flat) is { Success: true } zone ? zone.Groups["z"].Value : null,
            SubBand = MostFrequentSubBand(flat),
            Surface = SurfaceText().Match(flat) is { Success: true } surface ? ReadingText.Amount(surface.Groups["n"].Value) : null,
            Points = PointsText().Match(flat) is { Success: true } points ? int.Parse(points.Groups["n"].Value, CultureInfo.InvariantCulture) : null,
            Start = start,
            End = end,
            TermMonths = term.Success ? int.Parse(term.Groups["n"].Value, CultureInfo.InvariantCulture) : null,
            SignedOn = SignedOnText().Match(flat) is { Success: true } signed ? ReadingText.Date(signed.Groups["d"].Value) : null,
            Number = NumberText().Match(flat) is { Success: true } number ? number.Groups["n"].Value : null,
            FiscalCodes = ContractFiscalCodes.Find(text).Select(f => f.Code).ToList(),
        };
    }

    private static DateOnly? AgreementDateOf(string flat)
    {
        var match = AgreementDateText().Match(flat);
        if (!match.Success)
            return null;

        var raw = match.Groups["d"].Value;
        if (ReadingText.Date(raw) is { } numeric)
            return numeric;

        // "20 maggio 2025"
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var day) || !int.TryParse(parts[2], CultureInfo.InvariantCulture, out var year))
            return null;

        var month = Array.IndexOf(MonthNames, parts[1].ToLowerInvariant()) + 1;
        return month is >= 1 and <= 12 && year is >= 1970 and <= 2100 && day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }

    private static readonly string[] MonthNames =
        ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"];

    private static ConformityVerdict VerdictOf(string flat)
    {
        var within = AttestsWithin().IsMatch(flat);
        var not = AttestsNot().IsMatch(flat);
        return within == not ? ConformityVerdict.Unknown : within ? ConformityVerdict.Conform : ConformityVerdict.NotConform;
    }

    /// <summary>The sub-band the text names most often ("collocata nella sub-fascia 3"): the table lists all of them once, the verdict repeats one.</summary>
    internal static int? MostFrequentSubBand(string flat)
    {
        var counts = SubBandText().Matches(flat)
            .Select(m => int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture))
            .GroupBy(s => s)
            .Select(g => (Band: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();
        return counts.Count == 0 || (counts.Count > 1 && counts[0].Count == counts[1].Count) ? null : counts[0].Band;
    }
}
