using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily reminders of the supplier service payments (<c>service-payment-reminders</c>, 07:30 UTC), task SP-15b: a payment still to
/// be made is reminded at +2 and +7 days from its first request (<c>SupplierPayments:ReminderDays</c>), at most three emails with a
/// link in all and never two in a day, and it is flagged late <c>SupplierPayments:LateAfterDays</c> (7) days after it was asked for.
/// The payments that were pending (the supplier was not ready) and can go out now are sent too. See
/// <see cref="ISupplierPaymentJobService"/> and <c>docs/runbooks/hangfire.md</c>.
/// </summary>
/// <remarks>
/// Always scheduled. With the feature flag <c>SupplierOnlinePayments</c> off the run only flags the late payments: no request and no
/// reminder is sent (the flag stops what creates a payment request). Each payment is handled under the payment lock of its request
/// and read again after it, so a retry, a manual trigger or the supplier's own reminder never makes the payer receive a second one.
/// </remarks>
public class ServicePaymentRemindersJob(ISupplierPaymentJobService payments, ILogger<ServicePaymentRemindersJob> logger)
{
    public const string RecurringJobId = "service-payment-reminders";

    /// <summary>Every day at 07:30 UTC.</summary>
    public const string Cron = "30 7 * * *";

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var run = await payments.RunRemindersAsync(cancellationToken);
        logger.LogInformation(
            "Service payment reminders: {MarkedLate} flagged late, {RequestsSent} pending request(s) sent, {RemindersSent} reminder(s) sent, {Skipped} skipped, {Errors} error(s) (emails {Emails})",
            run.MarkedLate,
            run.RequestsSent,
            run.RemindersSent,
            run.Skipped,
            run.Errors,
            run.EmailsEnabled ? "on" : "off");
    }
}
