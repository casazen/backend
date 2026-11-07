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
        decimal priorOnFirst)
    {
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
    /// Any prior already written on a rate is cleared first, so a new choice cannot bill the same balance twice.
    /// Carry-forward and refund leave no prior on a rate. The next-installment choice uses the next rate that is
    /// not already paid.
    /// </summary>
    public static IReadOnlyList<Row> ApplyRecovery(
        IReadOnlyList<Row> issued,
        decimal amount,
        PriorYearBalanceDisposition disposition,
        DateOnly separateDue)
    {
        ArgumentNullException.ThrowIfNull(issued);
        var cleared = issued
            .Where(row => !row.IsPriorOnly)
            .Select(row => row with { Prior = 0m })
            .ToList();
        return disposition switch
        {
            PriorYearBalanceDisposition.CarryForward or PriorYearBalanceDisposition.Refund => cleared,
            PriorYearBalanceDisposition.SeparateInstallment => WithSeparateRow(issued, cleared, amount, separateDue),
            _ => PutOnNextUnpaid(cleared, amount),
        };
    }

    private static IReadOnlyList<Row> PutOnNextUnpaid(List<Row> cleared, decimal amount)
    {
        if (amount == 0m)
            return cleared;

        var target = cleared.FirstOrDefault(row => !row.Paid);
        if (target is null)
            return cleared;

        return cleared.Select(row => row.SlotIndex == target.SlotIndex ? row with { Prior = amount } : row).ToList();
    }

    private static IReadOnlyList<Row> WithSeparateRow(IReadOnlyList<Row> issued, List<Row> cleared, decimal amount, DateOnly due)
    {
        var existing = issued.FirstOrDefault(row => row.IsPriorOnly);
        var start = new DateOnly(due.Year, due.Month, 1);
        var label = $"Conguaglio · {Months[due.Month - 1]} {due.Year}";
        if (existing is not null)
        {
            cleared.Add(existing with
            {
                Label = label,
                PeriodStart = start,
                DueDate = due,
                Prior = amount,
                Ordinary = 0m,
                Extraordinary = 0m,
            });
            return cleared;
        }

        var slot = issued.Count == 0 ? 0 : issued.Max(row => row.SlotIndex) + 1;
        cleared.Add(new Row(slot, label, start, due, 0m, amount, 0m));
        return cleared;
    }
}
