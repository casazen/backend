using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The job of the money CasaZen holds for the services of the suppliers (<c>service-funds</c>, every five minutes), task SP-15c-a: it releases
/// to the supplier the money whose time has come (the payer said "all fine", or <c>SupplierPayments:ReleaseAfterMinutes</c> after the work was
/// done) and tries again the transfers that failed, with a growing delay (Stripe does not retry a failed transfer); it settles the money of the
/// requests that were refused, cancelled or lapsed; it drops the card blocks that outlived <c>SupplierPayments:AuthorizationValidityHours</c>;
/// and it warns the admins about money held for nearly <c>SupplierPayments:MaxHoldDays</c>. See <see cref="ISupplierFundsJobService"/> and
/// <c>docs/runbooks/hangfire.md</c>. Since SP-15c-c the same run follows the complaints about the jobs (<see cref="IServiceDisputeJobService"/>): it sends
/// to the admins the disputes whose supplier did not reply in time, tells them when their target passed or when the refund of a decision did not go
/// through, and opens the complaint for a job nobody marked completed once the time agreed for it ended.
/// </summary>
/// <remarks>
/// Always scheduled, whatever the state of the feature flag <c>SupplierOnlinePayments</c>: it follows money that is already in flight. Each
/// payment is handled under the payment lock of its request and read again after it, so a run, a retry, a manual trigger or the payer's own
/// "all fine" never pays a supplier twice nor refunds money that was released; an error on one payment is logged and never stops the others.
/// The disputes run <b>after</b> the releases, so a complaint that stopped a payment since the last run is seen by the release, which refuses it, and a
/// failure of the dispute step never keeps the releases from running.
/// </remarks>
public class ServiceFundsJob(ISupplierFundsJobService funds, IServiceDisputeJobService disputes, ILogger<ServiceFundsJob> logger)
{
    public const string RecurringJobId = "service-funds";

    /// <summary>Every 5 minutes.</summary>
    public const string Cron = "*/5 * * * *";

    // One run at a time; a second run started anyway (a retry, the dashboard) waits for the first and then finds nothing left to do.
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await funds.RunFundsAsync(cancellationToken);
        if (run.HadWork)
        {
            logger.LogInformation(
                "Service funds: {Released} released, {ReleaseFailed} release(s) waiting or refused, {Settled} closed request(s) settled, {Lapsed} block(s) released, {Warnings} hold warning(s), {Errors} error(s)",
                run.Released,
                run.ReleaseFailed,
                run.ClosedSettled,
                run.AuthorizationsLapsed,
                run.HoldWarnings,
                run.Errors);
        }

        // The disputes are a step of their own: whatever it finds, the releases above have run.
        var disputeRun = await disputes.RunDisputesAsync(cancellationToken);
        if (!disputeRun.HadWork)
            return;

        logger.LogInformation(
            "Service disputes: {Reached} sent to the admins, {Overdue} overdue, {RefundAlerts} refund alert(s), {NoShows} no-show complaint(s) opened, {Errors} error(s)",
            disputeRun.ReachedAdmins,
            disputeRun.Overdue,
            disputeRun.RefundAlerts,
            disputeRun.NoShowsOpened,
            disputeRun.Errors);
    }
}
