namespace Casazen.Core.Suppliers;

/// <summary>
/// The commission CasaZen keeps on a service paid inside CasaZen (SP-15a, decisions D2 and D3). Pure functions, no
/// configuration and <b>no percentage</b>: the percentage always comes in from the caller (the supplier's own override, else
/// <c>SupplierPayments:CommissionPercent</c>), and the payment keeps the figures it was created with.
/// </summary>
/// <remarks>
/// <para>The commission is a percentage of the price the payer pays, rounded to the cent away from zero
/// (<see cref="MidpointRounding.AwayFromZero"/>: half a cent goes up). It is sent to Stripe as <c>application_fee_amount</c>,
/// which Stripe accepts only when it is strictly between zero and the amount. A commission that rounds to nothing, or that is
/// not strictly below the amount, is therefore <b>not charged and not sent</b> (<see cref="FeeCents"/> returns 0), and the
/// parameter is left out of the request instead of being sent as an explicit zero (A3-40).</para>
/// </remarks>
public static class SupplierCommission
{
    /// <summary>
    /// The commission, in cents, that is really charged on <paramref name="amountCents"/> at <paramref name="percent"/>:
    /// <c>amount × percent / 100</c> rounded away from zero, or <c>0</c> when that is not strictly between zero and the amount
    /// (no commission is sent then).
    /// </summary>
    public static int FeeCents(int amountCents, decimal percent)
    {
        if (amountCents <= 0 || percent <= 0m)
            return 0;

        var fee = decimal.Round(amountCents * percent / 100m, 0, MidpointRounding.AwayFromZero);
        return fee > 0m && fee < amountCents ? (int)fee : 0;
    }

    /// <summary>What the supplier receives before Stripe's own fees: the amount minus the commission that is charged.</summary>
    public static int NetCents(int amountCents, int feeCents) => amountCents - feeCents;

    /// <summary>True when a commission of <paramref name="feeCents"/> goes to Stripe with a payment of <paramref name="amountCents"/>: strictly between zero and the amount.</summary>
    public static bool IsSendable(long feeCents, long amountCents) => feeCents > 0 && feeCents < amountCents;
}
