using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Condominium;

/// <summary>
/// Reads the text of a free-form administrator PDF and proposes a budget or a final statement.
/// It does not post. A line it is not sure of is not a tenant charge.
/// </summary>
public static partial class AdministratorStatementReader
{
    public const decimal ChargeConfidence = 0.85m;

    public sealed record Proposal(
        CondominiumStatementKind Kind,
        string Label,
        decimal? Ordinary,
        decimal? Extraordinary,
        string? ExtraordinaryDescription,
        decimal? FinalAmount,
        PriorYearBalanceDisposition? Settlement,
        decimal Confidence,
        bool ChargesTenant,
        string Excerpt,
        IReadOnlyList<string> NotExtracted);

    /// <summary>What this reader never takes from a free-form administrator PDF.</summary>
    public static readonly IReadOnlyList<string> NeverExtracted =
    [
        "singole voci di spesa",
        "millesimi",
        "pagamenti",
        "saldo precedente",
        "piano rate",
    ];

    public static bool TryRead(string? text, int fiscalStartMonth, out Proposal? proposal, out string? error)
    {
        proposal = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "no-text";
            return false;
        }

        if (StatementFile.HasMarker(text) && StatementFile.TryParse(text, out var structured, out _) && structured is not null)
        {
            proposal = FromStructured(structured);
            return true;
        }

        if (!TryKind(text, out var kind, out var kindPenalty))
        {
            error = "kind";
            return false;
        }

        if (!TryYear(text, fiscalStartMonth, out var label, out var yearPenalty))
        {
            error = "label";
            return false;
        }

        var confidence = 0.92m - kindPenalty - yearPenalty;
        var excerpts = new List<string>();
        decimal? ordinary = null;
        decimal? extraordinary = null;
        decimal? finalAmount = null;
        if (kind == CondominiumStatementKind.Budget)
        {
            if (!TryLabeled(text, OrdinaryLabels, out ordinary, out var ordinaryCount, out var ordinaryExcerpt))
            {
                error = "ordinary";
                return false;
            }

            confidence -= Penalty(ordinaryCount);
            excerpts.Add(ordinaryExcerpt);
            if (TryLabeled(text, ExtraordinaryLabels, out var extra, out var extraCount, out var extraExcerpt))
            {
                extraordinary = extra;
                confidence -= Penalty(extraCount);
                excerpts.Add(extraExcerpt);
            }
        }
        else if (!TryLabeled(text, FinalLabels, out finalAmount, out var finalCount, out var finalExcerpt))
        {
            error = "amount";
            return false;
        }
        else
        {
            confidence -= Penalty(finalCount);
            excerpts.Add(finalExcerpt);
        }

        var settlement = TrySettlement(text, out var settlementConflict);
        if (settlementConflict)
        {
            // Two dispositions are not a choice. Confirm must not invent one.
            settlement = null;
        }
        else if (kind == CondominiumStatementKind.Final)
        {
            // A final that names none is carried forward, and that choice is stored here.
            // Confirm then posts the stored disposition and does not guess.
            settlement ??= PriorYearBalanceDisposition.CarryForward;
        }

        confidence = Math.Max(0m, confidence);
        var charges = confidence >= ChargeConfidence;
        proposal = new Proposal(
            kind,
            label,
            ordinary,
            extraordinary,
            extraordinary is null ? null : "Spesa straordinaria",
            finalAmount,
            settlement,
            confidence,
            charges,
            ExcerptOf(excerpts),
            NeverExtracted);
        return true;
    }

    private static Proposal FromStructured(StatementFile.Model model) =>
        new(
            model.Kind,
            model.Label,
            model.Kind == CondominiumStatementKind.Budget ? model.Ordinary : null,
            model.Extraordinary > 0m ? model.Extraordinary : null,
            model.ExtraordinaryDescription,
            model.FinalAmount,
            model.Kind == CondominiumStatementKind.Final
                ? model.Settlement ?? PriorYearBalanceDisposition.CarryForward
                : model.Settlement,
            0.95m,
            true,
            StatementFile.Marker,
            NeverExtracted);

    private static bool TryKind(string text, out CondominiumStatementKind kind, out decimal penalty)
    {
        var preventivo = PreventivoPattern().IsMatch(text);
        var consuntivo = ConsuntivoPattern().IsMatch(text);
        penalty = 0m;
        if (preventivo && !consuntivo)
        {
            kind = CondominiumStatementKind.Budget;
            return true;
        }

        if (consuntivo && !preventivo)
        {
            kind = CondominiumStatementKind.Final;
            return true;
        }

        if (!preventivo && !consuntivo && RendicontoPattern().IsMatch(text))
        {
            kind = CondominiumStatementKind.Final;
            penalty = 0.15m;
            return true;
        }

        kind = default;
        return false;
    }

    private static bool TryYear(string text, int fiscalStartMonth, out string label, out decimal penalty)
    {
        label = string.Empty;
        penalty = 0m;
        var explicitYear = EsercizioPattern().Match(text);
        if (explicitYear.Success && Accept(explicitYear.Groups[1].Value, fiscalStartMonth, out label))
            return true;

        var years = YearPattern().Matches(text)
            .Select(match => match.Groups[1].Value + "/" + match.Groups[2].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(value => Accept(value, fiscalStartMonth, out _))
            .ToList();
        if (years.Count == 1 && Accept(years[0], fiscalStartMonth, out label))
            return true;

        if (TryDateRange(text, fiscalStartMonth, out label))
        {
            penalty = 0.12m;
            return true;
        }

        return false;
    }

    private static bool TryDateRange(string text, int fiscalStartMonth, out string label)
    {
        label = string.Empty;
        var dates = new List<DateOnly>();
        foreach (Match match in DatePattern().Matches(text))
        {
            if (TryCalendarDate(match, out var date))
                dates.Add(date);
        }

        dates = dates.Distinct().Order().ToList();
        if (dates.Count < 2)
            return false;

        for (var year = dates[0].Year - 1; year <= dates[^1].Year; year++)
        {
            var bounds = FiscalYear.ForStartYear(year, fiscalStartMonth);
            if (dates.Contains(bounds.Start) && dates.Contains(bounds.End))
            {
                label = bounds.Label;
                return true;
            }
        }

        return false;
    }

    private static bool TryCalendarDate(Match match, out DateOnly date)
    {
        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            date = default;
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    private static bool Accept(string value, int fiscalStartMonth, out string label)
    {
        var normalized = value.Replace('-', '/').Replace(" ", string.Empty, StringComparison.Ordinal);
        if (!FiscalYear.TryParseLabel(normalized, fiscalStartMonth, out var bounds, out _))
        {
            label = string.Empty;
            return false;
        }

        label = bounds.Label;
        return true;
    }

    private static bool TryLabeled(string text, string[] labels, out decimal? amount, out int count, out string excerpt)
    {
        amount = null;
        count = 0;
        excerpt = string.Empty;
        foreach (var label in labels)
        {
            var found = AmountsAfter(text, label);
            if (found.Count == 0)
                continue;
            amount = found[0].Amount;
            count = found.Count;
            excerpt = found[0].Excerpt;
            return true;
        }

        return false;
    }

    private static List<(decimal Amount, string Excerpt)> AmountsAfter(string text, string label)
    {
        var found = new List<(decimal Amount, string Excerpt)>();
        foreach (Match match in AmountAfterPattern(label).Matches(text))
        {
            if (TryMoney(match.Groups[1].Value, out var amount))
                found.Add((amount, match.Value.Trim()));
        }

        return found;
    }

    private static decimal Penalty(int count) => count > 1 ? 0.30m : 0m;

    private static PriorYearBalanceDisposition? TrySettlement(string text, out bool conflict)
    {
        var found = new List<PriorYearBalanceDisposition>();
        if (NextPattern().IsMatch(text))
            found.Add(PriorYearBalanceDisposition.NextInstallment);
        if (SeparatePattern().IsMatch(text))
            found.Add(PriorYearBalanceDisposition.SeparateInstallment);
        if (RefundPattern().IsMatch(text))
            found.Add(PriorYearBalanceDisposition.Refund);
        if (CarryPattern().IsMatch(text))
            found.Add(PriorYearBalanceDisposition.CarryForward);
        conflict = found.Count > 1;
        return found.Count == 1 ? found[0] : null;
    }

    private static bool TryMoney(string text, out decimal amount)
    {
        amount = 0m;
        var normalized = text.Trim().Replace("€", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (normalized.Contains(',', StringComparison.Ordinal))
        {
            normalized = normalized.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.');
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount)
            && CondominiumMoney.InRange(amount)
            && amount > 0m;
    }

    private static string ExcerptOf(IReadOnlyList<string> parts)
    {
        var text = string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return text.Length <= 500 ? text : text[..500];
    }

    private static readonly string[] OrdinaryLabels =
    [
        @"quota\s*ordinaria",
        @"preventivo\s*ordinario",
        @"spese\s*ordinarie",
        @"totale\s*ordinario",
        @"importo\s*ordinario",
        @"totale\s*preventivo",
    ];

    private static readonly string[] ExtraordinaryLabels =
    [
        @"quota\s*straordinaria",
        @"spese\s*straordinarie",
        @"totale\s*straordinario",
        @"lavori\s*straordinari",
    ];

    private static readonly string[] FinalLabels =
    [
        @"quota\s*consuntivo",
        @"totale\s*consuntivo",
        @"importo\s*consuntivo",
        @"totale\s*a\s*consuntivo",
        @"consuntivo\s*totale",
    ];

    private static Regex AmountAfterPattern(string label) =>
        new(label + @"[:\s]{0,30}((?:€\s*)?(?:\d{1,3}(?:\.\d{3})*,\d{2}|\d+,\d{2}|\d+\.\d{2}))", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"preventiv[oi]", RegexOptions.IgnoreCase)]
    private static partial Regex PreventivoPattern();

    [GeneratedRegex(@"consuntiv[oi]", RegexOptions.IgnoreCase)]
    private static partial Regex ConsuntivoPattern();

    [GeneratedRegex(@"rendiconto", RegexOptions.IgnoreCase)]
    private static partial Regex RendicontoPattern();

    [GeneratedRegex(@"esercizi[oa]\s*(20\d{2}\s*[/\-]\s*20\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex EsercizioPattern();

    [GeneratedRegex(@"(?<![0-9])(20\d{2})\s*[/\-]\s*(20\d{2})(?![0-9])")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\b(\d{1,2})[./](\d{1,2})[./](20\d{2})\b")]
    private static partial Regex DatePattern();

    [GeneratedRegex(@"prossim[oa]\s+rat[ae]", RegexOptions.IgnoreCase)]
    private static partial Regex NextPattern();

    [GeneratedRegex(@"rata\s+a\s+parte", RegexOptions.IgnoreCase)]
    private static partial Regex SeparatePattern();

    [GeneratedRegex(@"\brimborso\b", RegexOptions.IgnoreCase)]
    private static partial Regex RefundPattern();

    [GeneratedRegex(@"\briporto\b", RegexOptions.IgnoreCase)]
    private static partial Regex CarryPattern();
}
