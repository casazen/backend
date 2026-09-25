using Casazen.Infrastructure.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily deferred charge of "Paga alla scadenza" bookings (recurring job <c>direct-booking-charge</c>, 06:00 UTC), task
/// BK-08 (A3-14): off-session charge on the host's connected account from the free refund deadline, payment state from
/// Stripe, emails and limited attempts on failure, cancellation of what stays unpaid. See <see cref="DeferredChargeService"/>
/// and <c>docs/runbooks/direct-booking.md</c> § 9.
/// </summary>
/// <remarks>
/// <see cref="DisableConcurrentExecutionAttribute"/> keeps two runs (a retry, a manual trigger) from overlapping; each
/// booking is also locked in PostgreSQL, so a second run or instance never charges it twice, and at most one attempt per
/// booking per Europe/Rome day is made. Errors of one booking are logged and never stop the others; the job does not
/// throw for them, so Hangfire does not rerun the whole batch.
/// </remarks>
public class DirectBookingChargeJob(DeferredChargeService deferredCharges, ILogger<DirectBookingChargeJob> logger)
{
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)] // never two charging runs at once
    public async Task ExecuteAsync()
    {
        var run = await deferredCharges.RunDueChargesAsync(CancellationToken.None);
        logger.LogInformation(
            "Direct booking charge job: {Succeeded} paid, {Processing} processing, {AwaitingGuest} waiting for the guest, {Cancelled} cancelled, {Errors} error(s), {Skipped} skipped",
            run.Succeeded,
            run.Processing,
            run.AwaitingGuest,
            run.Cancelled,
            run.Errors,
            run.Skipped);
    }
}
