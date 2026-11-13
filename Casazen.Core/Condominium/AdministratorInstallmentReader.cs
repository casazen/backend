using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Services;

namespace Casazen.Core.Condominium;

/// <summary>Proposes unambiguous date/amount rows. The caller must review the apartment share before posting.</summary>
public static partial class AdministratorInstallmentReader
{
    public sealed record Reading(IReadOnlyList<CustomOwnerInstallment> Installments, string Excerpt, IReadOnlyList<string> NotExtracted);

    public static Reading Read(string? text)
    {
        var rows = new List<CustomOwnerInstallment>();
        var skipped = new List<string>();
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dates = DatePattern().Matches(line);
            if (dates.Count != 1) continue;
            var match = dates[0];
            if (!DateOnly.TryParseExact(match.Value, ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            var tail = line[(match.Index + match.Length)..];
            var amounts = MoneyPattern().Matches(tail);
            if (amounts.Count != 1 || !decimal.TryParse(amounts[0].Value, NumberStyles.Number, CultureInfo.GetCultureInfo("it-IT"), out var amount)
                || amount <= 0m || !CondominiumMoney.InRange(amount) || rows.Any(row => row.DueDate == date))
            {
                skipped.Add(line);
                continue;
            }
            rows.Add(new CustomOwnerInstallment(date, amount, 0m));
        }
        return new Reading(rows.OrderBy(row => row.DueDate).Take(366).ToList(),
            string.Join('\n', (text ?? "").Split('\n').Take(60)).Truncate(4000), skipped.Take(20).ToList());
    }

    private static string Truncate(this string value, int length) => value.Length <= length ? value : value[..length];

    [GeneratedRegex(@"\b(?:\d{1,2}[/-]\d{1,2}[/-]\d{4}|\d{4}-\d{2}-\d{2})\b")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"(?<![\d.,-])(?:\d{1,3}(?:\.\d{3})+|\d+),\d{2}(?![\d.,])")]
    private static partial Regex MoneyPattern();
}
