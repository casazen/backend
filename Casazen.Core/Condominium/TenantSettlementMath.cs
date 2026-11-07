using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Condominium;

/// <summary>
/// The tenant's consuntivo settlement:
/// recoverable final expenses of the contract + prior tenant balance − advances already posted.
/// Positive is a debit, negative a credit. Under € 0.01 is even. It is not the gap between budget and final.
/// A partial year uses the days the lease occupies the line, unless the contract names another rule.
/// </summary>
public static class TenantSettlementMath
{
    public const decimal EvenThreshold = 0.01m;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public sealed record LineInput(
        Guid LineId,
        decimal RecoverableAmount,
        DateOnly CompetenceStart,
        DateOnly CompetenceEnd,
        bool ChargesTenant);

    public sealed record Occupancy(DateOnly Start, DateOnly End);

    public sealed record Allocation(
        Guid LineId,
        TenantAllocationRule Rule,
        DateOnly? OccupancyStart,
        DateOnly? OccupancyEnd,
        int LineDays,
        int OccupiedDays,
        decimal BaseAmount,
        decimal AllocatedAmount,
        bool Charged);

    public sealed record Totals(decimal Amount, TenantSettlementDirection Direction);

    public sealed record SnapshotLine(
        Guid LineId,
        TenantAllocationRule Rule,
        int LineDays,
        int OccupiedDays,
        decimal BaseAmount,
        decimal AllocatedAmount,
        bool Charged);

    public sealed record SnapshotAdvance(Guid ComponentId, Guid EntryId, decimal Amount);

    public sealed record Snapshot
    {
        public Guid StatementId { get; init; }

        public int Revision { get; init; }

        public decimal Recoverable { get; init; }

        public decimal Prior { get; init; }

        public decimal Advances { get; init; }

        public decimal Amount { get; init; }

        public TenantSettlementDirection Direction { get; init; }

        public TenantSettlementDisposition Disposition { get; init; }

        public TenantAllocationRule Rule { get; init; }

        public List<SnapshotLine> Lines { get; init; } = [];

        public List<SnapshotAdvance> AdvancesDetail { get; init; } = [];

        public List<Guid> PriorSettlementIds { get; init; } = [];
    }

    /// <summary>
    /// Names the rule written on the lease. An empty clause is daily proration. A clause this version does not know
    /// is <see cref="TenantAllocationRule.ContractSpecific"/> and is not charged.
    /// </summary>
    public static TenantAllocationRule RuleFromContract(string? prorationRule)
    {
        if (string.IsNullOrWhiteSpace(prorationRule))
            return TenantAllocationRule.DailyProration;

        return prorationRule.Trim().ToLowerInvariant() switch
        {
            "daily" or "giornaliera" or "giornaliero" or "giorni" or "dailyproration" => TenantAllocationRule.DailyProration,
            "monthly" or "mensile" or "mesi" or "monthlyproration" => TenantAllocationRule.MonthlyProration,
            "fixed" or "fisso" or "fixedcontractamount" => TenantAllocationRule.FixedContractAmount,
            "consumption" or "consumo" => TenantAllocationRule.Consumption,
            "explicit" or "esplicito" or "explicitamount" => TenantAllocationRule.ExplicitAmount,
            "none" or "non addebitabile" or "notchargeable" => TenantAllocationRule.NotChargeable,
            _ => TenantAllocationRule.ContractSpecific,
        };
    }

    public static IReadOnlyList<Allocation> Allocate(
        IReadOnlyList<LineInput> lines,
        Occupancy occupancy,
        TenantAllocationRule rule,
        IReadOnlyDictionary<Guid, decimal>? explicitAmounts = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (occupancy.End < occupancy.Start)
            throw new ArgumentOutOfRangeException(nameof(occupancy), "The lease ends before it starts.");

        var allocated = new List<Allocation>(lines.Count);
        foreach (var line in lines)
        {
            if (!line.ChargesTenant || rule is TenantAllocationRule.NotChargeable or TenantAllocationRule.ContractSpecific)
            {
                allocated.Add(Skipped(line, rule is TenantAllocationRule.ContractSpecific or TenantAllocationRule.NotChargeable
                    ? rule
                    : TenantAllocationRule.NotChargeable));
                continue;
            }

            if (line.CompetenceEnd < line.CompetenceStart)
            {
                allocated.Add(Skipped(line, TenantAllocationRule.NotChargeable));
                continue;
            }

            if (!Overlap(occupancy.Start, occupancy.End, line.CompetenceStart, line.CompetenceEnd, out var start, out var end))
            {
                allocated.Add(new Allocation(
                    line.LineId, rule, null, null, InclusiveDays(line.CompetenceStart, line.CompetenceEnd), 0, line.RecoverableAmount, 0m, false));
                continue;
            }

            var lineDays = InclusiveDays(line.CompetenceStart, line.CompetenceEnd);
            var occupiedDays = InclusiveDays(start, end);
            if (rule is TenantAllocationRule.Consumption or TenantAllocationRule.ExplicitAmount
                && (explicitAmounts is null || !explicitAmounts.ContainsKey(line.LineId)))
            {
                allocated.Add(Skipped(line, rule));
                continue;
            }

            var amount = AmountFor(line, rule, lineDays, occupiedDays, start, end, explicitAmounts);

            allocated.Add(new Allocation(
                line.LineId,
                rule,
                start,
                end,
                lineDays,
                occupiedDays,
                line.RecoverableAmount,
                amount,
                amount != 0m));
        }

        return allocated;
    }

    /// <summary>Recoverable + prior − advances. The absolute value under <see cref="EvenThreshold"/> is even.</summary>
    public static Totals Settle(decimal recoverable, decimal prior, decimal advances)
    {
        var raw = recoverable + prior - advances;
        if (decimal.Abs(raw) < EvenThreshold)
            return new Totals(0m, TenantSettlementDirection.Balanced);

        var amount = CondominiumMoney.Round(raw);
        if (amount == 0m)
            return new Totals(0m, TenantSettlementDirection.Balanced);

        return new Totals(amount, amount > 0m ? TenantSettlementDirection.Debit : TenantSettlementDirection.Credit);
    }

    public static string Serialize(Snapshot snapshot) => JsonSerializer.Serialize(snapshot, JsonOptions);

    /// <summary>Recomputes the settlement from the stored inputs. A snapshot that does not replay is refused.</summary>
    public static Snapshot Replay(string json)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("The settlement snapshot is empty.");
        var totals = Settle(snapshot.Recoverable, snapshot.Prior, snapshot.Advances);
        if (totals.Amount != snapshot.Amount || totals.Direction != snapshot.Direction)
            throw new InvalidOperationException("The settlement snapshot does not replay.");

        return snapshot;
    }

    public static int InclusiveDays(DateOnly start, DateOnly end) =>
        end < start ? 0 : end.DayNumber - start.DayNumber + 1;

    private static decimal AmountFor(
        LineInput line,
        TenantAllocationRule rule,
        int lineDays,
        int occupiedDays,
        DateOnly occupiedStart,
        DateOnly occupiedEnd,
        IReadOnlyDictionary<Guid, decimal>? explicitAmounts)
    {
        switch (rule)
        {
            case TenantAllocationRule.FixedContractAmount:
                return CondominiumMoney.Round(line.RecoverableAmount);
            case TenantAllocationRule.MonthlyProration:
                var lineMonths = MonthsCovered(line.CompetenceStart, line.CompetenceEnd);
                var occupiedMonths = MonthsCovered(occupiedStart, occupiedEnd);
                return Prorate(line.RecoverableAmount, occupiedMonths, lineMonths);
            case TenantAllocationRule.Consumption:
            case TenantAllocationRule.ExplicitAmount:
                if (explicitAmounts is null || !explicitAmounts.TryGetValue(line.LineId, out var stated))
                    return 0m;
                return CondominiumMoney.Round(stated);
            case TenantAllocationRule.DailyProration:
                return Prorate(line.RecoverableAmount, occupiedDays, lineDays);
            default:
                return 0m;
        }
    }

    private static decimal Prorate(decimal amount, int numerator, int denominator)
    {
        if (denominator <= 0 || numerator <= 0)
            return 0m;
        if (numerator >= denominator)
            return CondominiumMoney.Round(amount);
        return CondominiumMoney.Round(amount * numerator / denominator);
    }

    private static int MonthsCovered(DateOnly start, DateOnly end) =>
        end < start ? 0 : ((end.Year - start.Year) * 12) + end.Month - start.Month + 1;

    private static bool Overlap(
        DateOnly aStart, DateOnly aEnd, DateOnly bStart, DateOnly bEnd, out DateOnly start, out DateOnly end)
    {
        start = aStart > bStart ? aStart : bStart;
        end = aEnd < bEnd ? aEnd : bEnd;
        return end >= start;
    }

    private static Allocation Skipped(LineInput line, TenantAllocationRule rule) =>
        new(line.LineId, rule, null, null, 0, 0, line.RecoverableAmount, 0m, false);
}
