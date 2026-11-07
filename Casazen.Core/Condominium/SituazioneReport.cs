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
