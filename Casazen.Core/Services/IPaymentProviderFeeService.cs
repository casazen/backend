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
    /// <summary>Reads and stores the figures of one payment, when it is eligible; the outcome says what happened.</summary>
    Task<PaymentProviderFeeOutcome> CaptureAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>
    /// The payments the job captures in one run, the oldest first, at most <paramref name="max"/>: card payments settled more than
    /// <see cref="PaymentProviderFees.SettleDelay"/> ago, completed or refunded, without figures and with fewer than
    /// <see cref="PaymentProviderFees.MaxAttempts"/> attempts.
    /// </summary>
    Task<IReadOnlyList<Guid>> DuePaymentIdsAsync(int max, CancellationToken ct = default);
}

/// <summary>What <see cref="IPaymentProviderFeeService.CaptureAsync"/> did.</summary>
public enum PaymentProviderFeeOutcome
{
    /// <summary>The figures were read and stored.</summary>
    Captured = 1,

    /// <summary>The figures were already there: nothing was read.</summary>
    AlreadyCaptured = 2,

    /// <summary>Not a settled card payment (no PaymentIntent, not completed or refunded), or not found: nothing was read.</summary>
    NotEligible = 3,

    /// <summary>The charge has no balance transaction yet: one attempt was counted.</summary>
    NotAvailableYet = 4,

    /// <summary>The read failed: one attempt was counted and the failure logged with the payment id.</summary>
    Failed = 5,

    /// <summary>The attempts are used up: nothing was read.</summary>
    AttemptsExhausted = 6,
}

/// <summary>The limits of the capture of the provider figures (E06/F04/US01/T02).</summary>
public static class PaymentProviderFees
{
    /// <summary>The reads of one payment before the capture gives up on it.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The payments one run of the job captures.</summary>
    public const int BatchSize = 50;

    /// <summary>How long after its settlement a payment is first read: Stripe writes the balance transaction shortly after the charge.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(10);
}
