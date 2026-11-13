using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Condominium;

/// <summary>
/// The owner's rate plan (CondoLedger <c>rate-plan.js</c>). Ordinary, prior-year balance and extraordinary are separate
/// columns. Building the plan the first time fills them. Applying a recovery choice afterwards changes only the prior
/// column, or adds one prior-only row. Ordinary and extraordinary of issued rows stay put.
/// </summary>
public static class OwnerInstallmentPlan
{
    private static readonly string[] Months = ["gen", "feb", "mar", "apr", "mag", "giu", "lug", "ago", "set", "ott", "nov", "dic"];

    public sealed record Row(
        int SlotIndex,
        string Label,
        DateOnly PeriodStart,
        DateOnly DueDate,
        decimal Ordinary,
        decimal Prior,
        decimal Extraordinary,
        bool Paid = false)
    {
        public decimal Total => CondominiumMoney.Round(Ordinary + Prior + Extraordinary);

        public bool IsPriorOnly =>
            Math.Abs(Ordinary) <= 0.005m && Math.Abs(Extraordinary) <= 0.005m && Math.Abs(Prior) > 0.005m;
    }

    public static IReadOnlyList<Row> Generate(
        DateOnly periodStart,
        CondominiumInstallmentCadence cadence,
        decimal ordinary,
        decimal extraordinary,
        decimal priorOnFirst,
        IReadOnlyList<DateOnly>? customDueDates = null)
    {
        if (!Enum.IsDefined(cadence)) throw new ArgumentOutOfRangeException(nameof(cadence));
        if (cadence == CondominiumInstallmentCadence.Custom)
        {
            if (customDueDates is not { Count: > 0 and <= 366 }
                || customDueDates.Distinct().Count() != customDueDates.Count
                || customDueDates.Any(d => d < periodStart || d >= periodStart.AddYears(1)))
                throw new ArgumentException("Custom dates must be unique dates within the fiscal year.");
            var dates = customDueDates.Order().ToList();
            var ordinaryCustom = CondominiumMoney.SplitEqually(ordinary, dates.Count);
            var extraordinaryCustom = CondominiumMoney.SplitEqually(extraordinary, dates.Count);
            return dates.Select((due, i) => new Row(i, $"Rata {i + 1}",
                i == 0 ? periodStart : dates[i - 1].AddDays(1), due, ordinaryCustom[i],
                i == 0 ? priorOnFirst : 0m, extraordinaryCustom[i])).ToList();
        }
        var (slots, step) = cadence switch
        {
            CondominiumInstallmentCadence.Bimonthly => (6, 2),
            CondominiumInstallmentCadence.Semiannual => (2, 6),
            CondominiumInstallmentCadence.Single => (1, 12),
            _ => (12, 1),
        };
        var months = Enumerable.Range(0, 12).Select(i => periodStart.AddMonths(i)).ToArray();
        var ordinaryParts = CondominiumMoney.SplitEqually(ordinary, slots);
        var extraordinaryParts = CondominiumMoney.SplitEqually(extraordinary, slots);
        var rows = new List<Row>(slots);
        for (var i = 0; i < slots; i++)
        {
            var start = months[Math.Min(i * step, months.Length - 1)];
            var due = new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));
            rows.Add(new Row(
                i,
                $"Rata {i + 1} · {Months[start.Month - 1]} {start.Year}",
                start,
                due,
                ordinaryParts[i],
                i == 0 ? priorOnFirst : 0m,
                extraordinaryParts[i]));
        }

        return rows;
    }

    /// <summary>
    /// Applies the owner's recovery choice. Ordinary and extraordinary of issued rows stay as they are.
    /// A paid rate is left as it was: its prior is not cleared and the same balance is not written onto another rate.
    /// What is not already on a paid rate is cleared from the open rates and written once, on the next unpaid rate
    /// or on one separate row. A cancelled separate row is that one row; a paid separate row is not reused.
    /// Carry-forward and refund add nothing further.
    /// </summary>
    public static IReadOnlyList<Row> ApplyRecovery(
        IReadOnlyList<Row> issued,
        decimal amount,
        PriorYearBalanceDisposition disposition,
        DateOnly separateDue)
    {
        ArgumentNullException.ThrowIfNull(issued);
        var paid = issued.Where(row => row.Paid).ToList();
        var open = issued
            .Where(row => !row.Paid && !row.IsPriorOnly)
            .Select(row => row with { Prior = 0m })
            .ToList();
        var kept = paid.Concat(open).OrderBy(row => row.SlotIndex).ToList();
        if (disposition is PriorYearBalanceDisposition.CarryForward or PriorYearBalanceDisposition.Refund)
            return kept;

        var locked = CondominiumMoney.Round(paid.Sum(row => row.Prior));
        var toPlace = CondominiumMoney.Round(amount - locked);
        if (toPlace == 0m)
            return kept;

        if (disposition == PriorYearBalanceDisposition.SeparateInstallment)
        {
            kept.Add(SeparateRow(issued, toPlace, separateDue));
            return kept.OrderBy(row => row.SlotIndex).ToList();
        }

        return PutOnNextUnpaid(kept, toPlace);
    }

    private static IReadOnlyList<Row> PutOnNextUnpaid(List<Row> rows, decimal amount)
    {
        if (amount == 0m)
            return rows;

        var target = rows.FirstOrDefault(row => !row.Paid);
        if (target is null)
            return rows;

        return rows.Select(row => row.SlotIndex == target.SlotIndex ? row with { Prior = amount } : row).ToList();
    }

    /// <summary>The open separate row, or a new slot when the previous one is paid. A paid row is not overwritten.</summary>
    private static Row SeparateRow(IReadOnlyList<Row> issued, decimal amount, DateOnly due)
    {
        var existing = issued.FirstOrDefault(row => row.IsPriorOnly && !row.Paid);
        var start = new DateOnly(due.Year, due.Month, 1);
        var label = $"Conguaglio · {Months[due.Month - 1]} {due.Year}";
        if (existing is not null)
        {
            return existing with
            {
                Label = label,
                PeriodStart = start,
                DueDate = due,
                Prior = amount,
                Ordinary = 0m,
                Extraordinary = 0m,
                Paid = false,
            };
        }

        var slot = issued.Count == 0 ? 0 : issued.Max(row => row.SlotIndex) + 1;
        return new Row(slot, label, start, due, 0m, amount, 0m);
    }
}
