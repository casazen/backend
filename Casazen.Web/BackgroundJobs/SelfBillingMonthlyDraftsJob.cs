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
/// The drafts of the self-billed invoices (TD17) of the month that ended (FIX/BE-TD17-ALERTS, FIX/BE-TD17-JOB-EXIT, E17/F01/US03, NF-116): <b>every day from the 5th to the 14th</b> at 07:30 Europe/Rome, for each
/// organization on the agency plan that has commissions in its owner ledger for the previous month, the same drafts as <c>POST api/fiscal/self-billing/drafts</c>
/// (<see cref="ISelfBillingService.CreateDraftsForPeriodAsync"/>, only the missing ones) and the «Da fare» «Autofatture da controllare ed emettere» (<see cref="ISelfBillingService.RunMonthlyDraftsAsync"/>).
/// It never issues: the issue stays a person's action.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>On by default.</b> A draft has no fiscal effect until a person issues it, and the TD17 is due by the 15th of the month after the foreign invoice is received (art. 1 c. 3-bis D.Lgs. 127/2015): the
/// real risk is not preparing it. It does nothing only when <c>SelfBilling:MonthlyDrafts:Enabled</c> is false or <c>Features:FiscalDocumentIssuance</c> is off (both read at each run): with either off it
/// returns before it opens a scope, so it reads no organization, takes no lock (the generation of the drafts takes the lock <c>SelfBilling</c> of each organization) and writes nothing.
/// It is registered while the issuance is on (and removed otherwise), like the other jobs of the fiscal documents.</item>
/// <item><b>Idempotent, so it can run every day.</b> A holder that already has a TD17 of the month (a draft, an issued one, a discarded or closed one) is not made again, and the «Da fare» is raised once
/// (while it is open; and never again for a month whose item a person closed, unless the run made a new draft): ten runs in a row, a retry or a trigger from the dashboard make no second draft for a holder
/// and no second item, and a draft a person discarded stays discarded. It never touches a document that is issued, rejected or closed.</item>
/// <item><b>A month with a holder refused is not a month done.</b> A holder that needs a TD17 and could not get one (its issuer profile is not complete) is counted by the run, logged as a warning
/// (codes and counts only) and <b>tried again by the next day's run</b>, so the draft appears the day after the profile is complete, before the deadline of the 15th; the organizations «made now» are the ones
/// that got a draft in this run (<c>Created</c> above 0), not the ones the run looked at. The drafts of the page of the self-billed invoices make the missing ones at any time.</item>
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
    /// Every day from the 5th to the 14th at 07:30 (the registration gives it the Europe/Rome clock): the month that ended is over everywhere, the owner ledger is usually posted in the first days, and the
    /// run goes on until the day before the deadline of the 15th (<c>SelfBilling:IssueByDay</c>), so a ledger posted on the 8th, or an issuer profile completed on the 9th, still gets its draft before it (decision 3 of
    /// the fiscal decisions of 2026-11-20, R3; ⚖ the days and the hour are a prudent default to be confirmed by the accountant). A run after the 14th is only a trigger by hand: the schedule never repeats it.
    /// </summary>
    public const string Cron = "30 7 5-14 * *";

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

        // The month that ended, by the calendar of Rome: every day from the 5th to the 14th of November runs October.
        var today = _clock.TodayInRomeAsDateOnly();
        var period = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);

        IReadOnlyList<Guid> orgIds;
        await using (var listing = scopeFactory.CreateAsyncScope())
            orgIds = await listing.ServiceProvider.GetRequiredService<ISelfBillingService>().OrgsWithCommissionsAsync(period.Year, period.Month, ct);

        var made = 0;
        var refusedOrganizations = 0;
        var refusedHolders = 0;
        var failures = new List<Exception>();
        foreach (var orgId in orgIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(orgId);
                var run = await scope.ServiceProvider.GetRequiredService<ISelfBillingService>().RunMonthlyDraftsAsync(orgId, period.Year, period.Month, ct);

                // «Made» is what got a draft in this run, not what the run looked at; the holders refused are counted too (their codes are logged by the run, for each organization).
                if (run.Created > 0)
                    made++;
                if (run.Refused > 0)
                {
                    refusedOrganizations++;
                    refusedHolders += run.Refused;
                }
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
        if (refusedHolders > 0)
        {
            // Not a month done: somebody needs a TD17 and cannot have one yet. The run does not fail for it (the next run, or the page of the self-billed invoices, makes the missing drafts).
            logger.LogWarning(
                "Self-billed drafts of {Year}-{Month}: {Organizations} organizations have {Holders} holders that need a TD17 and could not get one; they are tried again by the next run",
                period.Year,
                period.Month,
                refusedOrganizations,
                refusedHolders);
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} organizations did not get the self-billed drafts of {period.Year}-{period.Month}", failures);
    }
}
