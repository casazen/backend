using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring sync of the supplier service payments in flight (<c>service-payment-sync</c>, every 15 minutes), task SP-15b: the
/// PaymentIntents of the payments Stripe is processing (e.g. a SEPA debit) are read again and applied as the webhook would, in case
/// an event was lost; the refunds that never got Stripe's answer are looked for on Stripe and sent again with the same idempotency
/// key; the refunds Stripe has not completed are read again. See <see cref="ISupplierPaymentJobService"/> and
/// <c>docs/runbooks/hangfire.md</c>.
/// </summary>
/// <remarks>
/// Always scheduled, whatever the state of the feature flag <c>SupplierOnlinePayments</c>: it follows money that is already in
/// flight. Each payment is handled under the payment lock of its request, so a run, a retry or a manual trigger never records a
/// payment or a refund twice; an error on one item is logged and never stops the others.
/// </remarks>
public class ServicePaymentSyncJob(ISupplierPaymentJobService payments, ILogger<ServicePaymentSyncJob> logger)
{
    public const string RecurringJobId = "service-payment-sync";

    /// <summary>Every 15 minutes.</summary>
    public const string Cron = "*/15 * * * *";

    // One run at a time; a second run started anyway (a retry, the dashboard) waits for the first and then finds nothing left to do.
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await payments.SynchronizeAsync(cancellationToken);
        if (run.PaymentsRead + run.RefundsRead + run.Errors == 0)
            return;

        logger.LogInformation(
            "Service payment sync: {PaymentsRead} payment(s) read ({PaymentsUpdated} changed), {RefundsRead} refund(s) read ({RefundsUpdated} changed), {Errors} error(s)",
            run.PaymentsRead,
            run.PaymentsUpdated,
            run.RefundsRead,
            run.RefundsUpdated,
            run.Errors);
    }
}
