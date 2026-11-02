namespace Casazen.Core.Suppliers;

/// <summary>What CasaZen asked Stripe for when it created the payment held by the platform: the snapshot Stripe's answers are checked against.</summary>
/// <param name="PaymentIntentId">The PaymentIntent recorded on the payment; null when none was recorded.</param>
/// <param name="AmountCents">The price of the payment: what the card is blocked for and then charged.</param>
/// <param name="Currency">The currency of the payment (<c>eur</c>).</param>
public sealed record HeldChargeExpectation(string? PaymentIntentId, int AmountCents, string Currency);

/// <summary>What Stripe reports for a PaymentIntent of the platform: the event's, or the answer of a call.</summary>
/// <param name="PaymentIntentId">The PaymentIntent (<c>pi_…</c>).</param>
/// <param name="AccountId">The connected account the event happened on; <b>null</b> for the platform itself, which is what a held payment is.</param>
/// <param name="AmountCents">The amount of the PaymentIntent.</param>
/// <param name="AmountCapturableCents">What can still be charged from the block.</param>
/// <param name="AmountReceivedCents">What was charged.</param>
/// <param name="Currency">The currency Stripe reports.</param>
/// <param name="ApplicationFeeCents">The application fee on the PaymentIntent, which must be none.</param>
public sealed record HeldChargeObservation(
    string PaymentIntentId,
    string? AccountId,
    long AmountCents,
    long AmountCapturableCents,
    long AmountReceivedCents,
    string? Currency,
    long? ApplicationFeeCents);

/// <summary>
/// The checks that stand between what Stripe says and a payment held by CasaZen (SP-15c-a, the sibling of
/// <see cref="ServiceChargeVerification"/> for the direct charge): a block or a charge is accepted <b>only if</b> it is on the platform
/// account, on the PaymentIntent CasaZen created, for exactly the price and the currency of the payment, and with <b>no</b> application fee
/// (the commission is what is not transferred, never a fee Stripe takes). Anything else is money that moved in a way nobody planned: the
/// payment goes to <c>NeedsReview</c> and an admin is told, never "paid". Pure, so every combination is a unit test.
/// </summary>
public static class HeldChargeVerification
{
    /// <summary>The transfer went to another account than the supplier's.</summary>
    public const string DestinationMismatch = "destination";

    /// <summary>The transfer was reversed (all or in part) by somebody.</summary>
    public const string ReversedMismatch = "reversed";

    /// <summary>
    /// What differs for a <b>block</b> (the PaymentIntent is <c>requires_capture</c>): the whole price is capturable and nothing is charged.
    /// In a stable order (account, amount, currency, fee, intent); empty when the payment may be recorded as authorized.
    /// </summary>
    public static IReadOnlyList<string> AuthorizationMismatches(HeldChargeExpectation expected, HeldChargeObservation observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        var amountWrong = observed.AmountCents != expected.AmountCents
                          || observed.AmountCapturableCents != expected.AmountCents
                          || observed.AmountReceivedCents != 0;
        return Check(expected, observed, amountWrong);
    }

    /// <summary>
    /// What differs for a <b>charge</b> (the PaymentIntent is <c>succeeded</c>): the whole price was received. In a stable order (account,
    /// amount, currency, fee, intent); empty when the payment may be recorded as held.
    /// </summary>
    public static IReadOnlyList<string> CaptureMismatches(HeldChargeExpectation expected, HeldChargeObservation observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        var amountWrong = observed.AmountCents != expected.AmountCents || observed.AmountReceivedCents != expected.AmountCents;
        return Check(expected, observed, amountWrong);
    }

    /// <summary>
    /// What differs for the <b>transfer</b> to the supplier: the amount CasaZen meant to release, the supplier's account, and not reversed.
    /// </summary>
    public static IReadOnlyList<string> TransferMismatches(
        long expectedAmountCents,
        string? expectedDestination,
        long observedAmountCents,
        string? observedDestination,
        bool reversed)
    {
        var mismatches = new List<string>(3);
        if (observedAmountCents != expectedAmountCents)
            mismatches.Add(ServiceChargeVerification.AmountMismatch);

        if (string.IsNullOrWhiteSpace(expectedDestination)
            || !string.Equals(expectedDestination, observedDestination, StringComparison.Ordinal))
            mismatches.Add(DestinationMismatch);

        if (reversed)
            mismatches.Add(ReversedMismatch);

        return mismatches;
    }

    private static List<string> Check(HeldChargeExpectation expected, HeldChargeObservation observed, bool amountWrong)
    {
        var mismatches = new List<string>(5);

        // A held payment lives on the platform: an event of a connected account is not about it.
        if (!string.IsNullOrWhiteSpace(observed.AccountId))
            mismatches.Add(ServiceChargeVerification.AccountMismatch);

        if (amountWrong)
            mismatches.Add(ServiceChargeVerification.AmountMismatch);

        if (!string.Equals(observed.Currency, expected.Currency, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(ServiceChargeVerification.CurrencyMismatch);

        // No fee is the only right answer: the commission of a held payment is not transferred, never sent to Stripe as a fee.
        if ((observed.ApplicationFeeCents ?? 0L) != 0L)
            mismatches.Add(ServiceChargeVerification.FeeMismatch);

        if (string.IsNullOrWhiteSpace(expected.PaymentIntentId)
            || !string.Equals(expected.PaymentIntentId, observed.PaymentIntentId, StringComparison.Ordinal))
            mismatches.Add(ServiceChargeVerification.IntentMismatch);

        return mismatches;
    }
}
