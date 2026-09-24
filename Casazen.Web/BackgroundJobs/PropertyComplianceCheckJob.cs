using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// Nightly compliance check of every published or suspended property (CO-06, A5-20, A5-36): each
/// <see cref="Core.Entities.Enums.PropertyComplianceStatus.Active"/> or
/// <see cref="Core.Entities.Enums.PropertyComplianceStatus.Suspended"/> property is evaluated again with the activation
/// blockers (<see cref="IPropertyComplianceStatusService.RecalculateAllAsync"/>); an active one that lost a requirement
/// is suspended and its host gets one email, a suspended one whose requirements are complete again is reactivated. Safety net for what no request re-evaluates (a rule, required-document
/// list or declaration text changed by a deploy, a row changed outside the API). Its first run after the CO-06 deploy is
/// the recalculation of the historic properties. Also runnable once from the command line,
/// <c>compliance:recalculate [--dry-run]</c>. Runbooks: <c>docs/runbooks/compliance.md</c>,
/// <c>docs/runbooks/hangfire.md</c>.
/// </summary>
public class PropertyComplianceCheckJob(IPropertyComplianceStatusService complianceStatus)
{
    public const string RecurringJobId = "property-compliance-check";

    /// <summary>04:00 UTC (05:00 or 06:00 in Italy): after the nightly maintenance jobs, before the hosts' day.</summary>
    public const string Cron = "0 4 * * *";

    public const string CommandName = "compliance:recalculate";
    public const string DryRunFlag = "--dry-run";

    // Idempotent: only the Active -> Suspended transition emails the host, so a retry or a manual run sends no second
    // email for a property already suspended.
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken) =>
        await complianceStatus.RecalculateAllAsync(dryRun: false, cancellationToken);

    public static bool IsRecalculateCommand(string[] args) =>
        args.Contains(CommandName, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs <c>compliance:recalculate [--dry-run]</c> after the migrations: the same recalculation as the nightly job, now
    /// (or, with <c>--dry-run</c>, only the report). Prints one summary line and returns the exit code: 0 done, 2 another
    /// run in progress, 1 error.
    /// </summary>
    public static async Task<int> RunRecalculateCommandAsync(WebApplication app, string[] args)
    {
        var dryRun = args.Contains(DryRunFlag, StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var scope = app.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IPropertyComplianceStatusService>();
            var report = await service.RecalculateAllAsync(dryRun);
            if (report is null)
            {
                Console.WriteLine($"{CommandName}: another run is in progress, nothing done");
                return 2;
            }

            Console.WriteLine(
                $"{CommandName}{(dryRun ? " (dry run, nothing changed)" : string.Empty)}: " +
                $"checked={report.Checked} suspended={report.Suspended} reactivated={report.Reactivated} " +
                $"hostsNotified={report.HostsNotified} " +
                $"failed={report.Failed} blockers={string.Join(",", report.SuspendedByBlocker.Select(b => $"{b.Key}:{b.Value}"))}");
            return report.Failed > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Compliance recalculation command failed");
            return 1;
        }
    }
}
