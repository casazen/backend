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
        decimal Extraordinary)
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
    /// Applies the owner's recovery choice. Existing ordinary and extraordinary amounts are copied unchanged.
    /// Carry-forward and refund do not touch the rows.
    /// </summary>
    public static IReadOnlyList<Row> ApplyRecovery(
        IReadOnlyList<Row> issued,
        decimal amount,
        PriorYearBalanceDisposition disposition,
        DateOnly separateDue)
    {
        ArgumentNullException.ThrowIfNull(issued);
        return disposition switch
        {
            PriorYearBalanceDisposition.CarryForward or PriorYearBalanceDisposition.Refund => issued,
            PriorYearBalanceDisposition.SeparateInstallment => WithSeparateRow(issued, amount, separateDue),
            _ => PutOnNext(issued, amount),
        };
    }

    private static IReadOnlyList<Row> PutOnNext(IReadOnlyList<Row> issued, decimal amount)
    {
        if (issued.Count == 0)
            return issued;

        var target = issued.FirstOrDefault(row => !row.IsPriorOnly) ?? issued[0];
        return issued.Select(row => row with
        {
            Prior = row.SlotIndex == target.SlotIndex ? amount : 0m,
        }).ToList();
    }

    private static IReadOnlyList<Row> WithSeparateRow(IReadOnlyList<Row> issued, decimal amount, DateOnly due)
    {
        var existing = issued.FirstOrDefault(row => row.IsPriorOnly);
        if (existing is not null)
        {
            return issued.Select(row => row.SlotIndex == existing.SlotIndex ? row with { Prior = amount } : row).ToList();
        }

        var slot = issued.Count == 0 ? 0 : issued.Max(row => row.SlotIndex) + 1;
        var start = new DateOnly(due.Year, due.Month, 1);
        var added = new Row(slot, $"Conguaglio · {Months[due.Month - 1]} {due.Year}", start, due, 0m, amount, 0m);
        return issued.Concat([added]).ToList();
    }
}
