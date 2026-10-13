using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Sends the payment requests that waited for a supplier (<c>SendPendingPaymentRequestsJob(orgId)</c>), task SP-15b: queued when
/// <c>account.updated</c> says a supplier has just become able to take charges and payouts (BE-SP14-2). A request completed while the
/// supplier's Stripe account was not ready is a payment with no link; now the link is issued and emailed to the payer. See
/// <see cref="ISupplierPaymentJobService.SendPendingRequestsAsync"/>.
/// </summary>
/// <remarks>
/// Each payment is handled under the payment lock of its request and read again after it: a payment that was asked for in the
/// meantime (the supplier's own request, another run) is skipped, so the payer receives one email. With the feature flag
/// <c>SupplierOnlinePayments</c> off nothing is sent. The daily reminder job also sends the pending payments, so a job that failed or
/// was never queued is made up for.
/// </remarks>
public class SendPendingPaymentRequestsJob(ISupplierPaymentJobService payments)
{
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(Guid supplierOrgId, CancellationToken cancellationToken) =>
        await payments.SendPendingRequestsAsync(supplierOrgId, cancellationToken);
}

/// <summary>Queues <see cref="SendPendingPaymentRequestsJob"/> for a supplier that has just become ready.</summary>
public sealed class SupplierPaymentJobScheduler(
    IBackgroundJobClient backgroundJobClient,
    ILogger<SupplierPaymentJobScheduler> logger) : ISupplierPaymentJobScheduler
{
    public void SchedulePendingRequests(Guid supplierOrgId)
    {
        try
        {
            backgroundJobClient.Enqueue<SendPendingPaymentRequestsJob>(job => job.ExecuteAsync(supplierOrgId, CancellationToken.None));
        }
        catch (Exception ex)
        {
            // The event is already applied and must not fail for this: the daily reminder job sends the pending payments too.
            logger.LogError(ex, "The pending payment requests of supplier {SupplierOrgId} could not be queued", supplierOrgId);
        }
    }
}
