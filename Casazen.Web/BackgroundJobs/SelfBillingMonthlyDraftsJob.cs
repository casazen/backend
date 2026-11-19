using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The drafts of the self-billed invoices (TD17) of the month that ended (FIX/BE-TD17-ALERTS, E17/F01/US03, NF-116): on the 5th of every month at 07:30 Europe/Rome, for each organization on the
/// agency plan that has commissions in its owner ledger for the previous month, the same drafts as <c>POST api/fiscal/self-billing/drafts</c> (<see cref="ISelfBillingService.CreateDraftsForPeriodAsync"/>)
/// and the «Da fare» «Autofatture da controllare ed emettere» (<see cref="ISelfBillingService.RunMonthlyDraftsAsync"/>). It never issues: the issue stays a person's action.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Off by default.</b> It does nothing unless <c>SelfBilling:MonthlyDrafts:Enabled</c> is true and <c>Features:FiscalDocumentIssuance</c> is on (both read at each run): with either off it
/// returns before it opens a scope, so it reads no organization, takes no lock (the generation of the drafts takes the lock <c>SelfBilling</c> of each organization) and writes nothing.
/// It is registered while the issuance is on (and removed otherwise), like the other jobs of the fiscal documents.</item>
/// <item><b>Idempotent.</b> A month that already has a TD17 (a draft, an issued one, a discarded one) is not made again, and the «Da fare» is raised once while a draft is open: a retry, a second run
/// or a trigger from the dashboard makes no second draft and no second item, and a draft a person discarded stays discarded.</item>
/// <item><b>Each organization in a scope of its own, under its own tenant</b> (<see cref="IBackgroundTenantScope"/>): its own <c>DbContext</c>, so what one organization does never leaks into the
/// next, and an organization that fails does not stop the others.</item>
/// <item><b>Failures are not swallowed.</b> Once every organization was tried the run fails with them all (so Hangfire runs it again, and shows it in the schedule): the ones already made are
/// left alone by the second run. Never two runs at once.</item>
/// <item><b>Nothing personal in the log:</b> the ids of the organizations, the month, the codes and the counts; never a name, an amount or a VAT number.</item>
/// </list>
/// </remarks>
public class SelfBillingMonthlyDraftsJob(
    IServiceScopeFactory scopeFactory,
    IFeatureFlags featureFlags,
    IOptions<SelfBillingOptions> options,
    ILogger<SelfBillingMonthlyDraftsJob> logger,
    TimeProvider? timeProvider = null)
{
    public const string RecurringJobId = "self-billing-monthly-drafts";

    /// <summary>
    /// The 5th of every month at 07:30 (the registration gives it the Europe/Rome clock): the month that ended is over everywhere, the owner ledger is usually posted in the first days, and ten days are
    /// left before <c>SelfBilling:IssueByDay</c> (⚖: the day and the hour are a prudent default to be confirmed by the product owner). When the ledger of the month is not posted yet the run finds
    /// nothing (no organization has commissions), and the drafts are made from the page of the self-billed invoices.
    /// </summary>
    public const string Cron = "30 7 5 * *";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.MonthlyDrafts.Enabled)
        {
            logger.LogInformation("Self-billed drafts of the month: SelfBilling:MonthlyDrafts:Enabled is off, nothing is done");
            return;
        }

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
        {
            logger.LogInformation("Self-billed drafts of the month: the issuance is off, nothing is done");
            return;
        }

        // The month that ended, by the calendar of Rome: the 5th of November runs October.
        var today = _clock.TodayInRomeAsDateOnly();
        var period = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);

        IReadOnlyList<Guid> orgIds;
        await using (var listing = scopeFactory.CreateAsyncScope())
            orgIds = await listing.ServiceProvider.GetRequiredService<ISelfBillingService>().OrgsWithCommissionsAsync(period.Year, period.Month, ct);

        var made = 0;
        var failures = new List<Exception>();
        foreach (var orgId in orgIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(orgId);
                var run = await scope.ServiceProvider.GetRequiredService<ISelfBillingService>().RunMonthlyDraftsAsync(orgId, period.Year, period.Month, ct);
                if (!run.AlreadyMade)
                    made++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One organization does not stop the others: the run fails at the end so that Hangfire runs it again, and what was made is left alone by the second run.
                logger.LogError(
                    "Self-billed drafts of org {OrgId} for {Year}-{Month} not made ({Error}): the run is repeated",
                    orgId,
                    period.Year,
                    period.Month,
                    ex is DomainException refusal ? refusal.Code : ex.GetType().Name);
                failures.Add(ex);
            }
        }

        logger.LogInformation(
            "Self-billed drafts of {Year}-{Month}: {Organizations} organizations with commissions, {Made} made now, {Failed} failed",
            period.Year,
            period.Month,
            orgIds.Count,
            made,
            failures.Count);
        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} organizations did not get the self-billed drafts of {period.Year}-{period.Month}", failures);
    }
}
