using System.Text.RegularExpressions;

namespace Casazen.Core.Leases.Reading;

internal sealed record AgreedRent(decimal? Monthly, decimal? Annual, decimal? Minimum, decimal? Maximum);

internal static partial class AgreedRentReader
{
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private const int Timeout = 2000;
    private const string Eur = @"(?:\p{Sc}|\beuro\b|\beur\b\.?)";
    private const string Amount = ReadingText.AmountPattern;

    [GeneratedRegex(
        @"\bcanone\s+(?<monthly>mensile\s+)?convenuto(?:\s+(?:fra|tra)\s+le\s+parti)?\W{0,4}(?:\p{L}{1,4}'?\s+){0,3}?" + Eur + @"\s*(?<n>" + Amount + @")(?<after>[^.]{0,30})",
        Options,
        Timeout)]
    private static partial Regex Agreed();

    [GeneratedRegex(@"\bda\s+" + Eur + @"\s*(?<a>" + Amount + @")\s+a\s+" + Eur + @"\s*(?<b>" + Amount + @")\s+mensil", Options, Timeout)]
    private static partial Regex RangeFromTo();

    [GeneratedRegex(@"\bminimo\s+(?:di\s+)?" + Eur + @"\s*(?<a>" + Amount + @")\s+e\s+il\s+massimo\s+(?:di\s+)?" + Eur + @"\s*(?<b>" + Amount + @")", Options, Timeout)]
    private static partial Regex RangeMinMax();

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

        if (minimum is { } min && maximum is { } max && min > max)
            (minimum, maximum) = (null, null);

        return new AgreedRent(
            monthly.Count == 1 ? monthly.First() : null,
            annual.Count == 1 ? annual.First() : null,
            minimum,
            maximum);
    }
}
