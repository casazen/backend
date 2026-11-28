using System.Globalization;
using System.Text.RegularExpressions;

namespace Casazen.Core.Leases.Reading;

public sealed record CalculationSheetReading
{
    public string? Zone { get; init; }

    public string? OmiZone { get; init; }

    public int? SubBand { get; init; }

    public decimal? Surface { get; init; }

    public decimal? MonthlyRent { get; init; }

    public decimal? RangeMinimum { get; init; }

    public decimal? RangeMaximum { get; init; }

    public ConformityVerdict Verdict { get; init; }

    public DateOnly? Start { get; init; }

    public int? TermMonths { get; init; }

    public IReadOnlyList<string> FieldNames()
    {
        var names = new List<string>();
        void Add(string name, bool read)
        {
            if (read)
                names.Add(name);
        }

        Add("zone", Zone is not null);
        Add("subBand", SubBand is not null);
        Add("surface", Surface is not null);
        Add("rent", MonthlyRent is not null);
        Add("rentRange", RangeMinimum is not null && RangeMaximum is not null);
        Add("verdict", Verdict != ConformityVerdict.Unknown);
        Add("period", Start is not null || TermMonths is not null);
        return names;
    }
}

public static partial class CalculationSheetReader
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;

    [GeneratedRegex(@"\bzona\s+(?<z>\d{1,2})\b", Options, Timeout)]
    private static partial Regex ZoneText();

    [GeneratedRegex(@"\bzona\s+omi\s+(?<z>[A-Za-z]\d{1,2})\b", Options, Timeout)]
    private static partial Regex OmiZoneText();

    [GeneratedRegex(@"\bsuperficie\s+convenzionale\s+(?:di\s+)?mq\.?\s*(?<n>\d+(?:[.,]\d+)?)", Options, Timeout)]
    private static partial Regex SurfaceText();

    [GeneratedRegex(@"\bpertanto\s+non\s+conform|\bnon\s+(?:\w+\s+){0,2}conform", Options, Timeout)]
    private static partial Regex NotConformText();

    [GeneratedRegex(@"\bconform[ei]\s+all\W{0,2}\s*accordo", Options, Timeout)]
    private static partial Regex ConformText();

    [GeneratedRegex(@"\bdecorrenza\s+dal\s+(?<d>" + ReadingText.DatePattern + @")", Options, Timeout)]
    private static partial Regex StartText();

    [GeneratedRegex(@"(?<![\d/])(?<n>\d{1,3})\s+mesi\s+di\s+durata\s+iniziale", Options, Timeout)]
    private static partial Regex TermText();

    public static CalculationSheetReading Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new CalculationSheetReading();

        var flat = LeaseContractTextParser.Flatten(text);
        var rent = AgreedRentReader.Read(flat);
        var notConform = NotConformText().IsMatch(flat);
        var conform = ConformText().IsMatch(flat);
        var term = TermText().Match(flat);

        return new CalculationSheetReading
        {
            Zone = ZoneText().Match(flat) is { Success: true } zone ? zone.Groups["z"].Value : null,
            OmiZone = OmiZoneText().Match(flat) is { Success: true } omi ? omi.Groups["z"].Value.ToUpperInvariant() : null,
            SubBand = AttestationReader.MostFrequentSubBand(flat),
            Surface = SurfaceText().Match(flat) is { Success: true } surface ? ReadingText.Amount(surface.Groups["n"].Value) : null,
            MonthlyRent = rent.Monthly ?? (rent.Annual is { } year ? decimal.Round(year / 12m, 2) : null),
            RangeMinimum = rent.Minimum,
            RangeMaximum = rent.Maximum,
            Verdict = notConform == conform ? ConformityVerdict.Unknown : conform ? ConformityVerdict.Conform : ConformityVerdict.NotConform,
            Start = StartText().Match(flat) is { Success: true } start ? ReadingText.Date(start.Groups["d"].Value) : null,
            TermMonths = term.Success ? int.Parse(term.Groups["n"].Value, CultureInfo.InvariantCulture) : null,
        };
    }
}
