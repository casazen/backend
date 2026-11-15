using Casazen.Core.Entities;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// One piece of the withholding evidence of a payout (E18/F01/US02, NF-117): the base and the withholding of one stay, in euro, with the owner, the month and the stay it is about.
/// The annual data of the owners, the owner portal and the exports read the evidence through these pieces, so they all say the same euro.
/// </summary>
internal sealed record EvidenceLine(
    Guid EvidenceId,
    Guid OrgId,
    Guid OwnerId,
    int Month,
    DateOnly PaidOn,
    bool Reversal,
    string? TaxCode,
    string RuleVersion,
    Guid? BookingId,
    Guid? PropertyId,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int? Nights,
    decimal GrossBase,
    decimal Withheld);

/// <summary>
/// The pieces of one decision of the withholding, or of its reversal (E18/F01/US02, NF-117). The decision is taken once on the whole base of the stays of a payout
/// (<see cref="PayoutFactRules.Decide"/>), so the part of the withholding of each stay is not in the evidence: it is the part of the decision in proportion to the base of
/// the stay, to the cent, the cents that are left going to the largest remainders, so <b>the pieces always add up to the evidence, to the cent</b>. A reversal has the same pieces with
/// the opposite amounts. The evidence whose facts name no stay (a payout of before the facts were read from the ledger), or whose stays do not give its base, has one piece with no stay
/// for what the stays do not explain: its amounts are never lost and never invented.
/// </summary>
internal static class TaxEvidenceLines
{
    public static IReadOnlyList<EvidenceLine> Of(TaxObligationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var grossBase = evidence.GrossBase ?? 0m;
        var withheld = evidence.Amount ?? 0m;
        var sign = evidence.ReversesEvidenceId is null ? 1m : -1m;
        var stays = (PayoutFactRules.FromJson(evidence.FactsJson)?.Stays ?? []).Where(s => s.InBase).ToList();
        if (stays.Count == 0)
            return [WithoutStay(evidence, grossBase, withheld)];

        var weights = stays.Select(s => Cents(s.BaseAmount ?? 0m)).ToArray();
        var parts = Split(Math.Abs(Cents(withheld)), weights);
        var lines = new List<EvidenceLine>(stays.Count + 1);
        for (var index = 0; index < stays.Count; index++)
        {
            var stay = stays[index];
            lines.Add(new EvidenceLine(
                evidence.Id,
                evidence.OrgId,
                evidence.PropertyOwnerId,
                evidence.PeriodMonth,
                evidence.PaidOn,
                evidence.ReversesEvidenceId is not null,
                evidence.TaxCode,
                RuleVersionOf(evidence),
                stay.BookingId,
                stay.PropertyId,
                stay.CheckIn,
                stay.CheckOut,
                stay.Nights,
                sign * FromCents(weights[index]),
                sign * FromCents(parts[index])));
        }

        // What the stays do not explain of the base (a rule that reads another component than the gross of the stays): one piece of its own, so the base is the evidence's.
        var rest = grossBase - lines.Sum(l => l.GrossBase);
        if (rest != 0m)
            lines.Add(WithoutStay(evidence, rest, 0m));

        return lines;
    }

    /// <summary>The rule set a decision was taken on, as the evidence names it: <c>IT-STR-WITHHOLDING@2026.1</c>.</summary>
    public static string RuleVersionOf(TaxObligationEvidence evidence) => evidence.RuleCode + "@" + evidence.RuleVersion;

    /// <summary>
    /// <paramref name="total"/> cents split in proportion to <paramref name="weights"/>: each part is the share rounded down, and the cents that are left go one each to the
    /// largest remainders (the first of the stays among equal ones), so the parts always add up to <paramref name="total"/>. Without any weight the whole is the first part.
    /// </summary>
    internal static long[] Split(long total, long[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);

        var parts = new long[weights.Length];
        if (weights.Length == 0)
            return parts;

        var sum = weights.Where(w => w > 0).Sum();
        if (sum <= 0)
        {
            parts[0] = total;
            return parts;
        }

        var remainders = new (Int128 Remainder, int Index)[weights.Length];
        long given = 0;
        for (var index = 0; index < weights.Length; index++)
        {
            var product = (Int128)total * Math.Max(weights[index], 0);
            parts[index] = (long)(product / sum);
            remainders[index] = (product % sum, index);
            given += parts[index];
        }

        var left = total - given;
        foreach (var (_, index) in remainders.OrderByDescending(r => r.Remainder).ThenBy(r => r.Index).Take((int)left))
            parts[index]++;

        return parts;
    }

    internal static long Cents(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    internal static decimal FromCents(long cents) => cents / 100m;

    private static EvidenceLine WithoutStay(TaxObligationEvidence evidence, decimal grossBase, decimal withheld) =>
        new(
            evidence.Id,
            evidence.OrgId,
            evidence.PropertyOwnerId,
            evidence.PeriodMonth,
            evidence.PaidOn,
            evidence.ReversesEvidenceId is not null,
            evidence.TaxCode,
            RuleVersionOf(evidence),
            null,
            null,
            null,
            null,
            null,
            grossBase,
            withheld);
}
