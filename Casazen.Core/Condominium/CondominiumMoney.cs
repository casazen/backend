namespace Casazen.Core.Condominium;

/// <summary>Euro amounts in cents, the way CondoLedger rounds.</summary>
public static class CondominiumMoney
{
    public const decimal MaxAmount = 1_000_000m;

    public static decimal Round(decimal amount) =>
        decimal.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static bool IsCents(decimal amount) => amount == Round(amount);

    public static bool InRange(decimal amount) => IsCents(amount) && amount >= -MaxAmount && amount <= MaxAmount;

    /// <summary>
    /// Splits <paramref name="total"/> into <paramref name="parts"/> amounts. Leftover cents stay on the last part
    /// (CondoLedger <c>splitEqually</c>).
    /// </summary>
    public static IReadOnlyList<decimal> SplitEqually(decimal total, int parts)
    {
        if (parts < 1)
            throw new ArgumentOutOfRangeException(nameof(parts));

        var cents = (long)Round(total * 100m);
        var baseCents = cents / parts;
        var result = new decimal[parts];
        for (var i = 0; i < parts - 1; i++)
            result[i] = baseCents / 100m;
        result[^1] = (cents - (baseCents * (parts - 1))) / 100m;
        return result;
    }
}
