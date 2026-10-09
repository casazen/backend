using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Hourly application of the scheduled changes of rental mode (PM-02, D16): every change whose day has come (today in
/// Europe/Rome or before) is checked again against the stays, the imported calendar blocks and the leases and then applied,
/// or failed with the reason and the host told (<see cref="IPropertyModeService.ApplyDueAsync"/>). Hourly, not once a day,
/// so a change is applied within the hour after midnight of Rome whatever the offset from UTC, and a run lost to a restart is
/// made up at the next one. Idempotent: a change is applied once, whatever the number of runs, retries or manual triggers.
/// Registered only with <c>Features:PropertyModeChange</c> on. Runbook: <c>docs/runbooks/property-rental-mode.md</c>,
/// <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class PropertyModeChangeJob(IPropertyModeService propertyModeService)
{
    public const string RecurringJobId = "property-mode-change";

    /// <summary>Every hour, at minute 0 UTC (the same as <c>Hangfire.Cron.Hourly()</c>, <c>stay-alerts</c>): midnight of Rome is minute 0 of 22:00 or 23:00 UTC.</summary>
    public const string Cron = "0 * * * *";

    // Idempotent on its own (a change leaves "Scheduled" in the transaction of its effect, under the property lock), and one
    // run at a time by a session advisory lock in the service; this only keeps Hangfire from overlapping two runs.
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await propertyModeService.ApplyDueAsync(cancellationToken);
}
