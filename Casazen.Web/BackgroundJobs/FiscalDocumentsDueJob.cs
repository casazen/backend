using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Hangfire;
using Microsoft.Extensions.Options;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The documents of the stays that ended (E12/F01/US01/T02, NF-83, NF-112): every day at 05:10 Europe/Rome, for each organization with an issuer profile, a draft for every active
/// booking whose check-out day is before today that has no document (a portal rarely has a check-out recorded in CasaZen, so the event of the check-out alone is not enough); with
/// the automatic issue of the profile on, the drafts are issued through the same path as the API, otherwise they wait for the host (<see cref="IFiscalDocumentService.ProcessDueDocumentsAsync"/>).
/// </summary>
/// <remarks>
/// Registered only while <c>Features:FiscalDocumentIssuance</c> is on, and the run checks it again. Each organization runs in a dependency-injection scope of its own, under its own
/// tenant, with its own <c>DbContext</c> and under its own lock; an organization that fails does not stop the others, and what it did not do is done at the next run (idempotent: a
/// booking has one draft however many runs look at it). At most <see cref="MaxBookingsPerRun"/> stays for each organization in a run, the rest the next day, never further back than
/// <c>FiscalDocuments:DueLookbackDays</c>. Never two runs at once. The logs carry ids and counts, never a value of a document.
/// </remarks>
public class FiscalDocumentsDueJob(
    IServiceScopeFactory scopeFactory,
    IFeatureFlags featureFlags,
    IOptions<FiscalDocumentRulesOptions> options,
    ILogger<FiscalDocumentsDueJob> logger)
{
    public const string RecurringJobId = "fiscal-documents-due";

    /// <summary>Every day at 05:10 (the registration gives it the Europe/Rome clock): after the night, before the host's day.</summary>
    public const string Cron = "10 5 * * *";

    /// <summary>The most stays an organization gets looked at in one run.</summary>
    public const int MaxBookingsPerRun = 500;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(JobLockTimeouts.DefaultSeconds)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
        {
            logger.LogInformation("Fiscal documents due: the issuance is off, nothing is done");
            return;
        }

        IReadOnlyList<Guid> orgIds;
        await using (var listing = scopeFactory.CreateAsyncScope())
            orgIds = await listing.ServiceProvider.GetRequiredService<IFiscalDocumentService>().OrgsWithDocumentsDueAsync(ct);

        foreach (var orgId in orgIds)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(orgId);
                await scope.ServiceProvider.GetRequiredService<IFiscalDocumentService>()
                    .ProcessDueDocumentsAsync(orgId, MaxBookingsPerRun, options.Value.DueLookbackDays, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Fiscal documents due of org {OrgId} not processed ({Error}): read again at the next run", orgId, ex.GetType().Name);
            }
        }
    }
}
