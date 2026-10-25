using System.Text.RegularExpressions;

namespace Casazen.Core.Leases.Reading;

/// <summary>
/// The rent as the attestation and the calculation sheet state it (CF-DOCS-REAL): the rent agreed between the parties, monthly and yearly,
/// and the lowest and highest rent the territorial agreement allows for the house. Not read from the contract (that is
/// <see cref="LeaseContractTextParser"/>): from the sections that speak of the agreement.
/// </summary>
internal sealed record AgreedRent(decimal? Monthly, decimal? Annual, decimal? Minimum, decimal? Maximum);

/// <summary>Reads <see cref="AgreedRent"/> from the flattened text of an attestation or of a calculation sheet.</summary>
internal static partial class AgreedRentReader
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;
    private const string Eur = @"(?:\p{Sc}|\beuro\b|\beur\b\.?)";
    private const string Amount = ReadingText.AmountPattern;

    // "Canone mensile convenuto: EUR 595,00" · "Il canone convenuto fra le parti è di EUR 595,00 mensili" · "Canone convenuto EUR 595,00 mensili - EUR 7.140,00 annui".
    [GeneratedRegex(
        @"\bcanone\s+(?<monthly>mensile\s+)?convenuto(?:\s+(?:fra|tra)\s+le\s+parti)?\W{0,4}(?:\p{L}{1,4}'?\s+){0,3}?" + Eur + @"\s*(?<n>" + Amount + @")(?<after>[^.]{0,30})",
        Options,
        Timeout)]
    private static partial Regex Agreed();

    // "da EUR 594,00 a EUR 726,00 mensili"
    [GeneratedRegex(@"\bda\s+" + Eur + @"\s*(?<a>" + Amount + @")\s+a\s+" + Eur + @"\s*(?<b>" + Amount + @")\s+mensil", Options, Timeout)]
    private static partial Regex RangeFromTo();

    // "il minimo di EUR 594,00 e il massimo di EUR 726,00"
    [GeneratedRegex(@"\bminimo\s+(?:di\s+)?" + Eur + @"\s*(?<a>" + Amount + @")\s+e\s+il\s+massimo\s+(?:di\s+)?" + Eur + @"\s*(?<b>" + Amount + @")", Options, Timeout)]
    private static partial Regex RangeMinMax();

    // The rows of the table: "Canone minimo di sub-fascia EUR 594,00 mensili", "Canone massimo teorico EUR 726,00 mensili".
    [GeneratedRegex(@"\bcanone\s+minimo\b.{0,40}?" + Eur + @"\s*(?<n>" + Amount + @")\s+mensil", Options, Timeout)]
    private static partial Regex RowMinimum();

    [GeneratedRegex(@"\bcanone\s+massimo\b.{0,40}?" + Eur + @"\s*(?<n>" + Amount + @")\s+mensil", Options, Timeout)]
    private static partial Regex RowMaximum();

    [GeneratedRegex(@"^\s*mensil", Options, Timeout)]
    private static partial Regex MonthlyAfter();

    [GeneratedRegex(@"^\s*(?:annu|all'anno|l'anno|per\s+anno)", Options, Timeout)]
    private static partial Regex AnnualAfter();

    public static AgreedRent Read(string flat)
    {
        var monthly = new HashSet<decimal>();
        var annual = new HashSet<decimal>();

        foreach (Match match in Agreed().Matches(flat))
        {
            if (ReadingText.Amount(match.Groups["n"].Value) is not { } value)
                continue;

            var after = match.Groups["after"].Value;
            if (match.Groups["monthly"].Success || MonthlyAfter().IsMatch(after))
                monthly.Add(value);
            else if (AnnualAfter().IsMatch(after))
                annual.Add(value);
        }

        decimal? minimum = null;
        decimal? maximum = null;
        if (RangeFromTo().Match(flat) is { Success: true } range)
            (minimum, maximum) = (ReadingText.Amount(range.Groups["a"].Value), ReadingText.Amount(range.Groups["b"].Value));
        else if (RangeMinMax().Match(flat) is { Success: true } bounds)
            (minimum, maximum) = (ReadingText.Amount(bounds.Groups["a"].Value), ReadingText.Amount(bounds.Groups["b"].Value));
        else if (RowMinimum().Match(flat) is { Success: true } low && RowMaximum().Match(flat) is { Success: true } high)
            (minimum, maximum) = (ReadingText.Amount(low.Groups["n"].Value), ReadingText.Amount(high.Groups["n"].Value));

        // A range upside down is a misreading of one of its ends: neither is trusted.
        if (minimum is { } min && maximum is { } max && min > max)
            (minimum, maximum) = (null, null);

        return new AgreedRent(
            monthly.Count == 1 ? monthly.First() : null,
            annual.Count == 1 ? annual.First() : null,
            minimum,
            maximum);
    }
}
