namespace Casazen.Core.Condominium;

/// <summary>
/// The year as CondoLedger shows it: what was budgeted, what was spent, what was paid, and what is still open.
/// Paid and still-to-pay are two views of the same numbers.
/// </summary>
public static class SituazioneReport
{
    public sealed record InstallmentLine(
        Guid Id,
        int SlotIndex,
        string Label,
        DateOnly DueDate,
        decimal Ordinary,
        decimal Prior,
        decimal Extraordinary,
        decimal Due,
        decimal Paid)
    {
        public decimal Remaining => CondominiumMoney.Round(Due - Paid);

        public bool IsPaid => Math.Abs(Remaining) <= 0.01m && Paid > 0.005m;

        public bool IsOpen => Remaining > 0.01m;
    }

    public sealed record PaymentLine(Guid Id, DateOnly PaidOn, string? Method, decimal Amount, string Covers);

    public sealed record Year(
        string CondominiumName,
        string PeriodLabel,
        DateOnly PeriodStart,
        DateOnly PeriodEnd,
        decimal Ordinary,
        decimal Extraordinary,
        decimal Prior,
        decimal? FinalAmount,
        decimal Paid,
        Conguaglio.Result? Settlement,
        decimal StillToPay,
        IReadOnlyList<InstallmentLine> Installments,
        IReadOnlyList<PaymentLine> Payments);

    /// <summary>
    /// What each rate has received. A payment tied to a rate stays there. A year-level payment (no rate) is applied
    /// first to the ordinary rate whose window contains <c>PaidOn</c>. A separate conguaglio that shares that window
    /// does not take the payment: only the remainder reaches it, in due order. The id is not a tie-break.
    /// </summary>
    public static IReadOnlyDictionary<Guid, decimal> Allocate(
        IReadOnlyList<(Guid Id, DateOnly PeriodStart, DateOnly DueDate, decimal Due, bool PriorOnly)> installments,
        IReadOnlyList<(Guid? InstallmentId, DateOnly PaidOn, decimal Amount)> payments)
    {
        var paid = installments.ToDictionary(row => row.Id, _ => 0m);
        var order = installments
            .OrderBy(row => row.DueDate)
            .ThenBy(row => row.PriorOnly)
            .ThenBy(row => row.PeriodStart)
            .ToList();
        decimal pool = 0m;
        foreach (var payment in payments)
        {
            if (payment.InstallmentId is { } id && paid.ContainsKey(id))
            {
                paid[id] = CondominiumMoney.Round(paid[id] + payment.Amount);
                continue;
            }

            var left = payment.Amount;
            foreach (var row in order)
            {
                if (row.PriorOnly || row.PeriodStart > payment.PaidOn || payment.PaidOn > row.DueDate)
                    continue;
                var room = row.Due - paid[row.Id];
                if (room <= 0m || left <= 0m)
                    continue;
                var take = Math.Min(left, room);
                paid[row.Id] = CondominiumMoney.Round(paid[row.Id] + take);
                left = CondominiumMoney.Round(left - take);
            }

            if (left > 0m)
                pool = CondominiumMoney.Round(pool + left);
        }

        foreach (var row in order)
        {
            var room = row.Due - paid[row.Id];
            if (pool <= 0m || room <= 0m)
                continue;
            var take = Math.Min(pool, room);
            paid[row.Id] = CondominiumMoney.Round(paid[row.Id] + take);
            pool = CondominiumMoney.Round(pool - take);
        }

        return paid;
    }

    public static Year Build(
        string condominiumName,
        string periodLabel,
        DateOnly periodStart,
        DateOnly periodEnd,
        decimal ordinary,
        decimal extraordinary,
        decimal prior,
        decimal? finalAmount,
        IReadOnlyList<InstallmentLine> installments,
        IReadOnlyList<PaymentLine> payments)
    {
        var paid = CondominiumMoney.Round(payments.Sum(payment => payment.Amount));
        var settlement = finalAmount is > 0m
            ? Conguaglio.Compute(finalAmount.Value, extraordinary, prior, paid)
            : null;
        var still = settlement is not null
            ? (settlement.Direction == Conguaglio.Direction.Credit ? 0m : Math.Max(0m, settlement.Amount))
            : Math.Max(0m, CondominiumMoney.Round(ordinary + extraordinary + prior - paid));
        return new Year(
            condominiumName,
            periodLabel,
            periodStart,
            periodEnd,
            ordinary,
            extraordinary,
            prior,
            finalAmount,
            paid,
            settlement,
            still,
            installments,
            payments);
    }
}
