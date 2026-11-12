using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Condominium;

/// <summary>
/// A budget or final the owner would otherwise type, written as text so the same posting path can read a file.
/// This is not an interpretation of a free-form administrator PDF.
/// </summary>
public static partial class StatementFile
{
    public const string Marker = "condo-statement-v1";

    public sealed record Model(
        CondominiumStatementKind Kind,
        string Label,
        decimal Ordinary,
        decimal Extraordinary,
        string? ExtraordinaryDescription,
        decimal? FinalAmount,
        PriorYearBalanceDisposition? Settlement);

    public static bool HasMarker(string? text) =>
        text?.Contains(Marker, StringComparison.OrdinalIgnoreCase) == true;

    public static bool TryParse(string? text, out Model? model, out string? error)
    {
        model = null;
        error = null;
        if (!HasMarker(text))
        {
            error = "marker";
            return false;
        }

        var flat = text!.Replace('\r', ' ').Replace('\n', ' ');
        if (!TryKind(flat, out var kind))
        {
            error = "kind";
            return false;
        }

        var label = Group(flat, LabelPattern());
        if (string.IsNullOrWhiteSpace(label))
        {
            error = "label";
            return false;
        }

        if (kind == CondominiumStatementKind.Budget)
        {
            if (!TryMoney(Group(flat, OrdinaryPattern()), out var ordinary))
            {
                error = "ordinary";
                return false;
            }

            var extraordinary = 0m;
            var extraordinaryText = Group(flat, ExtraordinaryPattern());
            if (!string.IsNullOrWhiteSpace(extraordinaryText) && !TryMoney(extraordinaryText, out extraordinary))
            {
                error = "extraordinary";
                return false;
            }

            model = new Model(kind, label.Trim(), ordinary, extraordinary, Group(flat, ExtraordinaryDescriptionPattern()), null, null);
            return true;
        }

        if (!TryMoney(Group(flat, AmountPattern()), out var amount))
        {
            error = "amount";
            return false;
        }

        PriorYearBalanceDisposition? settlement = null;
        var settlementText = Group(flat, SettlementPattern());
        if (!string.IsNullOrWhiteSpace(settlementText))
        {
            if (!TrySettlement(settlementText, out var parsed))
            {
                error = "settlement";
                return false;
            }

            settlement = parsed;
        }

        model = new Model(kind, label.Trim(), 0m, 0m, null, amount, settlement);
        return true;
    }

    private static bool TryKind(string text, out CondominiumStatementKind kind)
    {
        var value = Group(text, KindPattern());
        if (value is "budget" or "preventivo")
        {
            kind = CondominiumStatementKind.Budget;
            return true;
        }

        if (value is "final" or "consuntivo")
        {
            kind = CondominiumStatementKind.Final;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TrySettlement(string value, out PriorYearBalanceDisposition disposition)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "next":
            case "prossima":
            case "scalato":
                disposition = PriorYearBalanceDisposition.NextInstallment;
                return true;
            case "separate":
            case "aparte":
                disposition = PriorYearBalanceDisposition.SeparateInstallment;
                return true;
            case "refund":
            case "rimborso":
                disposition = PriorYearBalanceDisposition.Refund;
                return true;
            case "carry":
            case "riporto":
                disposition = PriorYearBalanceDisposition.CarryForward;
                return true;
            default:
                disposition = default;
                return false;
        }
    }

    private static bool TryMoney(string? text, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var normalized = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Count(c => c == ',') == 1 && !normalized.Contains('.', StringComparison.Ordinal))
            normalized = normalized.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
            && CondominiumMoney.InRange(amount)
            && amount > 0m;
    }

    private static string? Group(string text, Regex pattern)
    {
        var match = pattern.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"kind:\s*(budget|preventivo|final|consuntivo)", RegexOptions.IgnoreCase)]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"label:\s*(\d{4}(?:/\d{4})?)", RegexOptions.IgnoreCase)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"ordinary:\s*([0-9][0-9\.,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinaryPattern();

    [GeneratedRegex(@"extraordinary:\s*([0-9][0-9\.,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraordinaryPattern();

    [GeneratedRegex(@"extraordinary_description:\s*([^:]+?)(?=\s+\w+:|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExtraordinaryDescriptionPattern();

    [GeneratedRegex(@"amount:\s*([0-9][0-9\.,]*)", RegexOptions.IgnoreCase)]
    private static partial Regex AmountPattern();

    [GeneratedRegex(@"settlement:\s*(next|prossima|scalato|separate|aparte|refund|rimborso|carry|riporto)", RegexOptions.IgnoreCase)]
    private static partial Regex SettlementPattern();
}
