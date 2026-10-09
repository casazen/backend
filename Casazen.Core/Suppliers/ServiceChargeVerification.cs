namespace Casazen.Core.Suppliers;

/// <summary>What CasaZen asked Stripe for when it created the payment: the snapshot a succeeded PaymentIntent is checked against.</summary>
/// <param name="ConnectedAccountId">The supplier's account the PaymentIntent was created on (<c>ServiceRequestPayment.ConnectedAccountId</c>).</param>
/// <param name="PaymentIntentId">The current PaymentIntent of the payment (<c>ServiceRequestPayment.StripePaymentIntentId</c>); null when none was recorded.</param>
/// <param name="AmountCents">The price of the payment.</param>
/// <param name="Currency">The currency of the payment (<c>eur</c>).</param>
/// <param name="ApplicationFeeCents">The commission that was sent to Stripe; 0 when none was sent.</param>
public sealed record ServiceChargeExpectation(
    string? ConnectedAccountId,
    string? PaymentIntentId,
    int AmountCents,
    string Currency,
    int ApplicationFeeCents);

/// <summary>What Stripe reports for a PaymentIntent that succeeded: the event's, or the one read back by the sync.</summary>
/// <param name="PaymentIntentId">The PaymentIntent (<c>pi_…</c>).</param>
/// <param name="AccountId">The connected account Stripe says it happened on (the event's <c>account</c>); null for the platform account itself.</param>
/// <param name="AmountCents">The amount of the PaymentIntent.</param>
/// <param name="AmountReceivedCents">The amount Stripe actually collected.</param>
/// <param name="Currency">The currency Stripe reports.</param>
/// <param name="ApplicationFeeCents">The application fee on the PaymentIntent; null when it has none.</param>
public sealed record ServiceChargeObservation(
    string PaymentIntentId,
    string? AccountId,
    long AmountCents,
    long AmountReceivedCents,
    string? Currency,
    long? ApplicationFeeCents);

/// <summary>
/// The check that stands between a Stripe <c>payment_intent.succeeded</c> and "Pagato" (SP-15b, decision D2): a service payment is
/// recorded as paid <b>only if</b> Stripe says it happened on the supplier's account the payment snapshotted, for the amount and
/// the currency of the payment, with the commission that was asked for, and on the PaymentIntent CasaZen created for it. Anything
/// else is money that arrived in a way nobody planned: the payment goes to <c>NeedsReview</c>, an admin is told, and the request is
/// never shown as paid. Pure, so every combination is a unit test.
/// </summary>
public static class ServiceChargeVerification
{
    /// <summary>The event comes from another Stripe account than the one the payment was created on (or from none).</summary>
    public const string AccountMismatch = "account";

    /// <summary>The amount, or the amount received, is not the price of the payment.</summary>
    public const string AmountMismatch = "amount";

    /// <summary>The currency is not the payment's.</summary>
    public const string CurrencyMismatch = "currency";

    /// <summary>The application fee is not the commission the payment snapshotted (a missing fee is a fee of zero).</summary>
    public const string FeeMismatch = "fee";

    /// <summary>The PaymentIntent is not the one CasaZen recorded on the payment (none recorded, or an older one).</summary>
    public const string IntentMismatch = "intent";

    /// <summary>Prefix of the <c>FailureCode</c> of a payment that needs a review.</summary>
    public const string ReviewPrefix = "review:";

    /// <summary>
    /// What differs, in a stable order (<see cref="AccountMismatch"/>, <see cref="AmountMismatch"/>, <see cref="CurrencyMismatch"/>,
    /// <see cref="FeeMismatch"/>, <see cref="IntentMismatch"/>); empty when the payment may be recorded as paid.
    /// </summary>
    public static IReadOnlyList<string> Mismatches(ServiceChargeExpectation expected, ServiceChargeObservation observed)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(observed);

        var mismatches = new List<string>(5);

        // The account is compared as the snapshot says: a payment with no recorded account can never be confirmed by an event.
        if (string.IsNullOrWhiteSpace(expected.ConnectedAccountId)
            || !string.Equals(expected.ConnectedAccountId, observed.AccountId, StringComparison.Ordinal))
            mismatches.Add(AccountMismatch);

        if (observed.AmountCents != expected.AmountCents || observed.AmountReceivedCents != expected.AmountCents)
            mismatches.Add(AmountMismatch);

        if (!string.Equals(observed.Currency, expected.Currency, StringComparison.OrdinalIgnoreCase))
            mismatches.Add(CurrencyMismatch);

        // No fee is the same as a fee of zero: the gateway never sends a zero, so Stripe reports none.
        if ((observed.ApplicationFeeCents ?? 0L) != expected.ApplicationFeeCents)
            mismatches.Add(FeeMismatch);

        if (string.IsNullOrWhiteSpace(expected.PaymentIntentId)
            || !string.Equals(expected.PaymentIntentId, observed.PaymentIntentId, StringComparison.Ordinal))
            mismatches.Add(IntentMismatch);

        return mismatches;
    }

    /// <summary>The <c>FailureCode</c> kept on a payment that needs a review: <c>review:account+fee</c>. No personal data, at most 100 characters.</summary>
    public static string ReviewCode(IReadOnlyList<string> mismatches)
    {
        ArgumentNullException.ThrowIfNull(mismatches);
        return ReviewPrefix + string.Join('+', mismatches);
    }
}
