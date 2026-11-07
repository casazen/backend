namespace Casazen.Core.Condominium;

/// <summary>
/// What is left to settle when the year closes (CondoLedger <c>computeConguaglio</c>):
/// consuntivo + straordinari + saldo riportato − versato.
/// Positive is a debit, negative a credit. It is not the gap between budget and final.
/// </summary>
public static class Conguaglio
{
    public enum Direction
    {
        Debit,
        Credit,
        Even,
    }

    public sealed record Result(
        decimal Amount,
        Direction Direction,
        decimal Consuntivo,
        decimal Straordinari,
        decimal Riportato,
        decimal Pagato);

    public static Result? Compute(decimal consuntivo, decimal straordinari, decimal riportato, decimal pagato)
    {
        if (consuntivo == 0m)
            return null;

        var amount = CondominiumMoney.Round(consuntivo + straordinari + riportato - pagato);
        var direction = amount > 0.005m
            ? Direction.Debit
            : amount < -0.005m
                ? Direction.Credit
                : Direction.Even;
        return new Result(amount, direction, consuntivo, straordinari, riportato, pagato);
    }
}
