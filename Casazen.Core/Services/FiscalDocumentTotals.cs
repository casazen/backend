using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// The one place that computes the amounts of a guest or direct-booking fiscal document (NF-83, AC-NF83-7, E06/F01/US02/T03): the net of every line,
/// the taxable amount and the VAT of every rate, the stamp duty and the total, from the amounts the guest pays (VAT included) and the approved rule.
/// The rounding is part of the versioned rule (<see cref="RuleVersion"/>), not inherited from the legacy agency invoices.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Rule <c>fiscal-totals-2026.11-1</c>.</b> The lines are grouped by VAT rate and nature code. For a group the guest paid <c>G</c>: the taxable
/// amount is <c>T = round(G × 100 / (100 + rate), 2)</c> (half away from zero), the VAT is <c>G − T</c>, so the document total is exactly what the guest
/// paid; the net of each line is computed the same way and the cent of difference, if any, goes to the largest line of its group, so the lines add up
/// to <c>T</c>. A group at rate zero has <c>T = G</c> and no VAT.</item>
/// <item><b>Stamp duty.</b> Due when the amounts without VAT exceed the threshold of the rule; it is added to the total only when the rule charges it
/// to the recipient.</item>
/// <item><b>Credit notes.</b> <see cref="ScaleForCredit"/> spreads an amount over the lines of the credited document in proportion, the cent of
/// difference on the largest line, never more than the document.</item>
/// </list>
/// </remarks>
public static class FiscalDocumentTotals
{
    /// <summary>The version of the computation, stored with the documents it computed.</summary>
    public const string RuleVersion = "fiscal-totals-2026.11-1";

    /// <summary>The code of amounts that cannot make a document (negative, nothing to bill, a rate out of range).</summary>
    public const string AmountsInvalidCode = "document_amounts_invalid";

    /// <summary>The amounts of a document whose lines the guest pays <paramref name="lines"/> (VAT included).</summary>
    public static FiscalDocumentAmounts FromGross(IReadOnlyList<FiscalDocumentGrossLine> lines, FiscalStampDutyRule stamp)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(stamp);
        if (lines.Count == 0
            || lines.Any(l => l.GrossAmount < 0m || l.Quantity <= 0m || l.VatRatePercent is < 0m or > 100m)
            || lines.Sum(l => l.GrossAmount) <= 0m)
        {
            throw new DomainRuleException(AmountsInvalidCode, "FiscalDocumentAmountsInvalid");
        }

        var computed = new FiscalDocumentLineAmounts[lines.Count];
        var summary = new List<FiscalDocumentVatSummary>();
        foreach (var group in lines
                     .Select((line, index) => (Line: line, Index: index))
                     .GroupBy(x => (x.Line.VatRatePercent, Nature: x.Line.VatRatePercent == 0m ? x.Line.NatureCode : null))
                     .OrderByDescending(g => g.Key.VatRatePercent)
                     .ThenBy(g => g.Key.Nature, StringComparer.Ordinal))
        {
            var rate = group.Key.VatRatePercent;
            var gross = group.Sum(x => x.Line.GrossAmount);
            var taxable = Net(gross, rate);
            var nets = group.Select(x => (x.Index, x.Line, Net: Net(x.Line.GrossAmount, rate))).ToList();
            var difference = taxable - nets.Sum(n => n.Net);
            if (difference != 0m)
            {
                var largest = nets.OrderByDescending(n => n.Line.GrossAmount).ThenBy(n => n.Index).First();
                nets[nets.IndexOf(largest)] = largest with { Net = largest.Net + difference };
            }

            foreach (var (index, line, net) in nets)
            {
                computed[index] = new FiscalDocumentLineAmounts(
                    index + 1,
                    line.Description,
                    line.Quantity,
                    line.Quantity == 1m ? net : Math.Round(net / line.Quantity, 8, MidpointRounding.AwayFromZero),
                    net,
                    rate,
                    rate == 0m ? line.NatureCode : null,
                    line.LineType);
            }

            summary.Add(new FiscalDocumentVatSummary(rate, group.Key.Nature, taxable, gross - taxable));
        }

        var withoutVat = summary.Where(s => s.VatRatePercent == 0m).Sum(s => s.TaxableAmount);
        var stampDuty = withoutVat > stamp.Threshold ? stamp.Amount : 0m;
        var taxableBase = summary.Sum(s => s.TaxableAmount);
        var vat = summary.Sum(s => s.Tax);
        var total = taxableBase + vat + (stamp.ChargedToRecipient ? stampDuty : 0m);

        return new FiscalDocumentAmounts(computed, summary, taxableBase, vat, stampDuty, total, RuleVersion);
    }

    /// <summary>
    /// The lines of a credit note of <paramref name="amount"/> (VAT included) over the <paramref name="original"/> lines of the credited document: each line
    /// in proportion to what it weighs, the cent of difference on the largest one. 422 when the amount is not positive or exceeds the lines.
    /// </summary>
    public static IReadOnlyList<FiscalDocumentGrossLine> ScaleForCredit(IReadOnlyList<FiscalDocumentGrossLine> original, decimal amount)
    {
        ArgumentNullException.ThrowIfNull(original);
        var whole = original.Sum(l => l.GrossAmount);
        if (original.Count == 0 || amount <= 0m || whole <= 0m || amount > whole)
            throw new DomainRuleException(AmountsInvalidCode, "FiscalDocumentAmountsInvalid");

        if (amount == whole)
            return original;

        var scaled = original
            .Select(l => l with { Quantity = 1m, GrossAmount = Math.Round(l.GrossAmount * amount / whole, 2, MidpointRounding.AwayFromZero) })
            .ToList();
        var difference = amount - scaled.Sum(l => l.GrossAmount);
        if (difference != 0m)
        {
            var largest = scaled.Select((l, i) => (Line: l, Index: i)).OrderByDescending(x => x.Line.GrossAmount).ThenBy(x => x.Index).First();
            scaled[largest.Index] = largest.Line with { GrossAmount = largest.Line.GrossAmount + difference };
        }

        return scaled.Where(l => l.GrossAmount > 0m).ToList();
    }

    /// <summary>The net of a gross amount at <paramref name="rate"/> percent, to the cent (half away from zero).</summary>
    public static decimal Net(decimal gross, decimal rate) =>
        rate == 0m ? Math.Round(gross, 2, MidpointRounding.AwayFromZero) : Math.Round(gross * 100m / (100m + rate), 2, MidpointRounding.AwayFromZero);
}

/// <summary>A line as the guest pays it (VAT included).</summary>
/// <param name="Description">What the line is (no personal data).</param>
/// <param name="Quantity">The quantity (1 for the lines of a stay).</param>
/// <param name="GrossAmount">The amount of the line, VAT included.</param>
/// <param name="VatRatePercent">The VAT rate of the line in percent; 0 for an operation without VAT.</param>
/// <param name="NatureCode">The nature code of an operation without VAT.</param>
/// <param name="LineType">What the line is: the tax rule gives its rate by type.</param>
public sealed record FiscalDocumentGrossLine(
    string Description,
    decimal Quantity,
    decimal GrossAmount,
    decimal VatRatePercent,
    string? NatureCode,
    FiscalLineType LineType = FiscalLineType.Accommodation);

/// <summary>A computed line, net of VAT.</summary>
public sealed record FiscalDocumentLineAmounts(
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TotalPrice,
    decimal VatRatePercent,
    string? NatureCode,
    FiscalLineType LineType = FiscalLineType.Accommodation);

/// <summary>One row of the VAT summary: the taxable amount and the VAT of a rate (and nature code).</summary>
public sealed record FiscalDocumentVatSummary(decimal VatRatePercent, string? NatureCode, decimal TaxableAmount, decimal Tax);

/// <summary>The amounts of a document, as <see cref="FiscalDocumentTotals"/> computed them.</summary>
public sealed record FiscalDocumentAmounts(
    IReadOnlyList<FiscalDocumentLineAmounts> Lines,
    IReadOnlyList<FiscalDocumentVatSummary> Summary,
    decimal TaxableBase,
    decimal VatAmount,
    decimal StampDuty,
    decimal Total,
    string RuleVersion);

/// <summary>The stamp duty of the rule: due above the threshold on the amounts without VAT; added to the total only when charged to the recipient.</summary>
public sealed record FiscalStampDutyRule(decimal Threshold, decimal Amount, bool ChargedToRecipient);
