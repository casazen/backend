using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Resends to Stripe a refund whose first submission ended with a timeout or a Stripe 5xx (BK-02). The request carries
/// the refund's own idempotency key, so Stripe either creates the refund now or returns the one it already created.
/// A transient failure is rethrown and Hangfire retries with growing delays; the refund stays pending (and its
/// amount reserved) until Stripe answers or a <c>refund.*</c> webhook links it.
/// </summary>
public sealed class PaymentRefundSubmitJob(IPaymentRefundService refundService)
{
    // No lock needed: two runs send the same idempotency key and get the same Stripe refund.
    [AutomaticRetry(Attempts = 10)]
    public Task SubmitAsync(Guid refundId) => refundService.SubmitPendingAsync(refundId);
}

/// <summary>Schedules <see cref="PaymentRefundSubmitJob"/> one minute after a transient failure.</summary>
public sealed class PaymentRefundRetryScheduler(
    IBackgroundJobClient backgroundJobClient,
    ILogger<PaymentRefundRetryScheduler> logger) : IPaymentRefundRetryScheduler
{
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);

    public void ScheduleSubmit(Guid refundId)
    {
        try
        {
            backgroundJobClient.Schedule<PaymentRefundSubmitJob>(job => job.SubmitAsync(refundId), Delay);
        }
        catch (Exception ex)
        {
            // The refund stays pending and visible; its refund.* webhook, if Stripe created it, still completes it.
            logger.LogError(ex, "Retry of refund {RefundId} could not be scheduled", refundId);
        }
    }
}
