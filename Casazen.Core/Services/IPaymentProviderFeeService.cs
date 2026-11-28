namespace Casazen.Core.Services;

/// <summary>
/// What the card provider kept and credited for a settled card payment of a booking (FIS-05, NF-85, E06/F04/US01/T02): the commission, the net, the
/// currency of the balance, the day the funds are available and the id of the balance transaction, read from Stripe and stored on the payment.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Information only.</b> The capture writes the <c>Provider*</c> columns of the payment and nothing else: never <c>Amount</c>,
/// <c>RefundedAmount</c> or <c>Status</c>.</item>
/// <item><b>Idempotent and bounded.</b> A payment whose figures were read is never read again; a read that finds no balance transaction yet (or fails)
/// counts an attempt, and after <see cref="PaymentProviderFees.MaxAttempts"/> the payment is left without figures, with one line in the log (the
/// payment id only).</item>
/// <item><b>Cent-accurate.</b> The minor units of Stripe are turned into amounts with the conversion of the refunds (<c>PaymentRefundService</c>).</item>
/// </list>
/// </remarks>
public interface IPaymentProviderFeeService
{
    Task<PaymentProviderFeeOutcome> CaptureAsync(Guid paymentId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> DuePaymentIdsAsync(int max, CancellationToken ct = default);
}

public enum PaymentProviderFeeOutcome
{
    Captured = 1,

    AlreadyCaptured = 2,

    NotEligible = 3,

    NotAvailableYet = 4,

    Failed = 5,

    AttemptsExhausted = 6,
}

public static class PaymentProviderFees
{
    public const int MaxAttempts = 5;

    public const int BatchSize = 50;

    /// <summary>How long after its settlement a payment is first read: Stripe writes the balance transaction shortly after the charge.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(10);
}
