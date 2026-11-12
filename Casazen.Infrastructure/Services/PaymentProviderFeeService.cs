using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.External;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IPaymentProviderFeeService"/>
/// <remarks>
/// <list type="bullet">
/// <item>The account of the read is the one the refunds use (<see cref="PaymentRefundService.ResolveStripeAccountAsync"/>): none for an intent on the
/// platform, the connected account of a direct charge.</item>
/// <item>The minor units become amounts with <see cref="PaymentRefundService.FromCents"/>, the conversion of the refunds, so a fee and a net are worth
/// to the cent what the balance transaction says.</item>
/// <item>A Stripe failure is never swallowed silently: it counts an attempt and is logged with the payment id and the Stripe error code (no personal
/// data); the job reads the payment again at its next run until the attempts are used up.</item>
/// </list>
/// </remarks>
public sealed class PaymentProviderFeeService(
    AppDbContext db,
    IStripeService stripe,
    TimeProvider timeProvider,
    ILogger<PaymentProviderFeeService> logger) : IPaymentProviderFeeService
{
    private static readonly PaymentStatus[] SettledStatuses = [PaymentStatus.Completed, PaymentStatus.Refunded, PaymentStatus.PartiallyRefunded];

    public async Task<IReadOnlyList<Guid>> DuePaymentIdsAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0)
            return [];

        var settledBefore = timeProvider.GetUtcNow().UtcDateTime - PaymentProviderFees.SettleDelay;
        return await db.Payments
            .AsNoTracking()
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(p => p.StripePaymentIntentId != null
                && SettledStatuses.Contains(p.Status)
                && p.ProcessedAt != null
                && p.ProcessedAt < settledBefore
                && p.ProviderFeeFetchedAt == null
                && p.ProviderFeeFetchAttempts < PaymentProviderFees.MaxAttempts)
            .OrderBy(p => p.ProcessedAt)
            .ThenBy(p => p.Id)
            .Select(p => p.Id)
            .Take(max)
            .ToListAsync(ct);
    }

    public async Task<PaymentProviderFeeOutcome> CaptureAsync(Guid paymentId, CancellationToken ct = default)
    {
        var payment = await db.Payments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null || string.IsNullOrWhiteSpace(payment.StripePaymentIntentId) || !SettledStatuses.Contains(payment.Status))
            return PaymentProviderFeeOutcome.NotEligible;
        if (payment.ProviderFeeFetchedAt is not null)
            return PaymentProviderFeeOutcome.AlreadyCaptured;
        if (payment.ProviderFeeFetchAttempts >= PaymentProviderFees.MaxAttempts)
            return PaymentProviderFeeOutcome.AttemptsExhausted;

        StripeBalanceTransactionInfo? transaction;
        try
        {
            var account = await PaymentRefundService.ResolveStripeAccountAsync(db, payment, ct);
            transaction = await stripe.GetChargeBalanceTransactionAsync(payment.StripePaymentIntentId, account, ct);
        }
        catch (StripeException ex)
        {
            await CountMissAsync(payment, ct);
            logger.LogWarning(
                "Provider fees of payment {PaymentId} not read (Stripe {ErrorCode}), attempt {Attempt} of {MaxAttempts}",
                payment.Id,
                ex.StripeError?.Code ?? "unknown",
                payment.ProviderFeeFetchAttempts,
                PaymentProviderFees.MaxAttempts);
            return PaymentProviderFeeOutcome.Failed;
        }

        if (transaction is null)
        {
            await CountMissAsync(payment, ct);
            return PaymentProviderFeeOutcome.NotAvailableYet;
        }

        // Only the provider columns: Amount, RefundedAmount and Status are never written here.
        payment.ProviderFeeAmount = PaymentRefundService.FromCents(transaction.FeeMinor);
        payment.ProviderNetAmount = PaymentRefundService.FromCents(transaction.NetMinor);
        payment.ProviderBalanceCurrency = transaction.Currency.Trim().ToUpperInvariant();
        payment.ProviderBalanceTransactionId = transaction.Id;
        payment.ProviderAvailableOn = transaction.AvailableOnUtc;
        payment.ProviderFeeFetchedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Provider fees of payment {PaymentId} captured from {BalanceTransactionId}", payment.Id, transaction.Id);
        return PaymentProviderFeeOutcome.Captured;
    }

    /// <summary>One more attempt; at the last one the capture gives up on the payment with one line in the log (the id only).</summary>
    private async Task CountMissAsync(Payment payment, CancellationToken ct)
    {
        payment.ProviderFeeFetchAttempts++;
        await db.SaveChangesAsync(ct);
        if (payment.ProviderFeeFetchAttempts >= PaymentProviderFees.MaxAttempts)
            logger.LogWarning("Provider fees of payment {PaymentId} not available after {MaxAttempts} attempts: left without them", payment.Id, PaymentProviderFees.MaxAttempts);
    }
}
