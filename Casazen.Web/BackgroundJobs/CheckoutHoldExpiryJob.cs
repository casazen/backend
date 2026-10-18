using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Recurring expiry of the public checkout holds past <c>DirectBooking:PendingTtlMinutes</c> (BK-21, A3-13): cancels
/// their PaymentIntent or SetupIntent on the host's connected account, then the booking
/// (<c>CancellationReason = CheckoutHoldExpired</c>). A hold whose guest has already paid or is paying is left to the
/// payment webhook. Host bookings are never touched. "Pay at the property" requests (D5, BK-06) past their own deadline
/// (<c>Booking.RequestExpiresAt</c>: email not confirmed in time, or no answer from the host) are cancelled by the same
/// routine, and the guest of an unanswered request gets an email. Runbooks: <c>docs/runbooks/hangfire.md</c>,
/// <c>docs/runbooks/direct-booking.md</c>.
/// </summary>
public class CheckoutHoldExpiryJob(
    ICheckoutHoldExpiryService expiryService,
    ICheckoutPaymentReconciliationService reconciliationService,
    ILogger<CheckoutHoldExpiryJob> logger)
{
    public const string RecurringJobId = "checkout-hold-expiry";

    /// <summary>Every 5 minutes: with the 15-minute TTL a hold is released at most 5 minutes late.</summary>
    public const string Cron = "*/5 * * * *";

    // One run at a time; a second run started anyway (e.g. from the dashboard) skips the holds the first one has locked.
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // First the payments the expiry left to the webhook and the webhook never settled: Stripe says if they were paid. The ones
        // never paid go back to pending, so the expiry below cancels them in the same run.
        var reconciled = await reconciliationService.ReconcileInFlightPaymentsAsync(cancellationToken);
        if (reconciled != CheckoutPaymentReconciliationRun.Empty)
        {
            logger.LogInformation(
                "Checkout payment reconciliation: {Settled} settled, {StillInFlight} still in flight, {Released} never paid, {Failed} failed",
                reconciled.Settled,
                reconciled.StillInFlight,
                reconciled.Released,
                reconciled.Failed);
        }

        var run = await expiryService.ExpireDueHoldsAsync(cancellationToken);
        if (run == CheckoutHoldExpiryRun.Empty)
            return;

        if (run.Failed > 0)
        {
            logger.LogWarning(
                "Checkout hold expiry: {Expired} expired, {LeftToWebhook} left to the payment webhook, {Skipped} skipped, {Failed} failed (retried by the next run)",
                run.Expired,
                run.LeftToWebhook,
                run.Skipped,
                run.Failed);
            return;
        }

        logger.LogInformation(
            "Checkout hold expiry: {Expired} expired, {LeftToWebhook} left to the payment webhook, {Skipped} skipped",
            run.Expired,
            run.LeftToWebhook,
            run.Skipped);
    }
}
