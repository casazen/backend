using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Logging;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Daily alert to hosts of Ticino properties within 24 hours of the police notification deadline (ADR-016).
/// Runs daily to check if reminders need to be sent for upcoming deadlines.
/// </summary>
public class TicinoReminderJob(
    ITicinoNotificationService ticinoNotificationService,
    ILogger<TicinoReminderJob> logger)
{
    public const string RecurringJobId = "ticino-reminder";

    /// <summary>08:00 UTC (09:00 or 10:00 in Switzerland).</summary>
    public const string Cron = "0 8 * * *";

    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync()
    {
        logger.LogInformation("Ticino reminder job starting");

        // TODO: Implement the actual reminder logic
        // This would involve:
        // 1. Finding all Ticino properties (CantonCode = TI)
        // 2. Checking if any have upcoming deadlines within 24 hours
        // 3. Sending reminder notifications to the property hosts
        // For now, this is a placeholder structure

        await Task.CompletedTask;

        logger.LogInformation("Ticino reminder job completed");
    }
}
