using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The monthly package of the accountant (E15/F01/US02/T02, NF-101, D11): every day at 06:00 Europe/Rome, each organization that turned the monthly send on and saved the address of its accountant gets the package
/// of the previous month made, kept in the private storage and announced to the accountant by an e-mail with a signed link (30 days, no attachment), from the day of the month it chose on, until the month is sent
/// (<see cref="IAccountantPackageDeliveryService"/>). The same run then deletes the packages whose link has expired.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Registered only while <c>Features:AccountantPackage</c> is on</b> (removed otherwise, so the schedule of an earlier deploy stops), and the run checks the flag again.</item>
/// <item><b>Once for each organization and month.</b> The service records the month it queued the e-mail for under the lock of the organization, so a retry, a second run on the same day, another instance and a
/// «send a test» at the same moment send nothing more; a run that finds nothing due does nothing.</item>
/// <item><b>Each organization in a scope of its own, under its own tenant</b> (<see cref="IBackgroundTenantScope"/>): its own <c>DbContext</c>, so what one org does never leaks into the next, and an
/// organization that fails does not stop the others.</item>
/// <item><b>Failures are not swallowed.</b> A failed send is recorded by the service in the settings with its code (<c>lastSend</c>) and logged; once every organization was tried the run fails with them all, so that
/// Hangfire runs it again (only the organizations still due are looked at) and the schedule shows it. Never two runs at once.</item>
/// <item><b>Nothing personal in the log:</b> the ids of the organizations, the month, the codes and the counts; never the address of an accountant, a link or a name.</item>
/// </list>
/// </remarks>
public class AccountantPackageMonthlyJob(
    IServiceScopeFactory scopeFactory,
    IFeatureFlags featureFlags,
    ILogger<AccountantPackageMonthlyJob> logger,
    TimeProvider? timeProvider = null)
{
    public const string RecurringJobId = "accountant-package-monthly";

    /// <summary>Every day at 06:00 (the registration gives it the Europe/Rome clock): after the documents of the night (05:10) and before the day of the agency.</summary>
    public const string Cron = "0 6 * * *";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.AccountantPackage))
        {
            logger.LogInformation("Accountant package monthly run: the feature is off, nothing is done");
            return;
        }

        var today = _clock.TodayInRomeAsDateOnly();
        IReadOnlyList<Guid> due;
        await using (var listing = scopeFactory.CreateAsyncScope())
            due = await listing.ServiceProvider.GetRequiredService<IAccountantPackageDeliveryService>().DueOrganizationsAsync(today, cancellationToken);

        var sent = 0;
        var failures = new List<Exception>();
        foreach (var orgId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(orgId);
                var outcome = await scope.ServiceProvider.GetRequiredService<IAccountantPackageDeliveryService>().SendMonthlyAsync(orgId, today, cancellationToken);
                if (outcome == AccountantPackageMonthlyOutcome.Sent)
                    sent++;
                else if (outcome == AccountantPackageMonthlyOutcome.Failed)
                    failures.Add(new InvalidOperationException($"The package of org {orgId} could not be sent: see its last send"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One organization does not stop the others; the run fails at the end so that Hangfire runs it again, and the month each organization already sent makes the second run safe.
                logger.LogError(ex, "Accountant package of org {OrgId} could not be sent", orgId);
                failures.Add(ex);
            }
        }

        var deleted = 0;
        try
        {
            await using var cleanup = scopeFactory.CreateAsyncScope();
            deleted = await cleanup.ServiceProvider.GetRequiredService<IAccountantPackageDeliveryService>().DeleteExpiredPackagesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Accountant package cleanup failed: the packages whose link has expired are deleted at the next run");
            failures.Add(ex);
        }

        logger.LogInformation(
            "Accountant package monthly run of {Today}: {Due} organizations due, {Sent} packages sent, {Deleted} expired packages deleted, {Failed} failed",
            today,
            due.Count,
            sent,
            deleted,
            failures.Count);
        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} steps of the monthly package of the accountant failed", failures);
    }
}
