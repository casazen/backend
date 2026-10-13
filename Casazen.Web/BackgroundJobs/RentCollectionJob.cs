using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily rent collection of long-term leases (recurring job <c>rent-collection</c>, 07:00 UTC), task LT-06 (#269, A7-07):
/// the payment link of each installment coming due is emailed to the tenants, and installments Stripe reported in flight
/// are read again in case their webhook was lost. See <see cref="IRentBillingService"/> and
/// <c>docs/runbooks/hangfire.md</c> § 12.
/// </summary>
/// <remarks>
/// Each installment is handled under the PostgreSQL lock of its lease and requested once (the request is recorded with
/// the installment), so a retry, a manual trigger or a second instance never emails it twice. Errors of one installment
/// are logged and never stop the others.
/// </remarks>
public class RentCollectionJob(IRentBillingService rentBilling, ILogger<RentCollectionJob> logger)
{
    public const string RecurringJobId = "rent-collection";
    public const string Cron = "0 7 * * *";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        var run = await rentBilling.RunCollectionAsync(CancellationToken.None);
        logger.LogInformation(
            "Rent collection job: {RequestsSent} payment request(s) sent, {Synchronized} in-flight payment(s) updated, {Errors} error(s)",
            run.RequestsSent,
            run.Synchronized,
            run.Errors);
    }
}
