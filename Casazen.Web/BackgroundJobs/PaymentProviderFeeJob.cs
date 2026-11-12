using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Reads what Stripe kept and credited for the settled card payments of the bookings (FIS-05, NF-85, E06/F04/US01/T02): every 15 minutes, the
/// payments settled more than ten minutes ago that have no provider figures and fewer than five attempts, at most 50 a run, one capture each
/// (<see cref="IPaymentProviderFeeService"/>). Never two runs at once. Without a Stripe key nothing is read and no attempt is counted, so the
/// payments keep their five attempts for when the key is configured.
/// </summary>
public class PaymentProviderFeeJob(
    IPaymentProviderFeeService fees,
    IConfiguration configuration,
    ILogger<PaymentProviderFeeJob> logger)
{
    public const string RecurringJobId = "payment-provider-fees";

    /// <summary>Every 15 minutes.</summary>
    public const string Cron = "*/15 * * * *";

    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(configuration["Stripe:SecretKey"]))
        {
            logger.LogInformation("Provider fees not read: Stripe is not configured in this environment");
            return;
        }

        var due = await fees.DuePaymentIdsAsync(PaymentProviderFees.BatchSize, ct);
        var captured = 0;
        foreach (var paymentId in due)
        {
            if (await fees.CaptureAsync(paymentId, ct) == PaymentProviderFeeOutcome.Captured)
                captured++;
        }

        if (due.Count > 0)
            logger.LogInformation("Provider fees: {Captured} of {Due} payments captured", captured, due.Count);
    }
}
