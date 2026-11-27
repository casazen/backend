using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The fallback of the webhook of the SDI channel (E17/F01/US02/T01, NF-116): every 30 minutes it asks the provider about every fiscal document <c>submitted</c> through it and records
/// what the SDI answered (delivered, rejected) through the one entry of the outcomes (<see cref="IFiscalDocumentSdiService.RecordOutcomeAsync"/>), under the tenant of the document's org.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Only with the key and the flags.</b> Registered only while <c>Features:FiscalDocumentIssuance</c> and <c>Features:SdiSubmission</c> are on, and the run checks them again, and that a provider
/// is configured (<c>Sdi:Provider</c>): otherwise it does nothing and writes nothing.</item>
/// <item><b>Never swallowed.</b> Each document runs in its own scope; one the provider cannot answer for (it is unreachable, it answers an error) is logged with its id and the type of the error and read
/// again at the next run, the others go on; at the end the run <b>fails</b> (<see cref="SdiStatusPollException"/>) when some document could not be read, so the run is visible as failed
/// and nothing is reported as done that was not. Never two runs at once. The logs carry ids and counts, never a value of a document or of a notification.</item>
/// </list>
/// </remarks>
public class SdiStatusPollJob(
    IServiceScopeFactory scopeFactory,
    IFeatureFlags featureFlags,
    ILogger<SdiStatusPollJob> logger)
{
    public const string RecurringJobId = "fiscal-sdi-status";

    /// <summary>Every 30 minutes.</summary>
    public const string Cron = "*/30 * * * *";

    /// <summary>The most documents one run asks about; the rest are asked at the next run (the oldest state first).</summary>
    public const int MaxDocumentsPerRun = 200;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance) || !featureFlags.IsEnabled(FeatureFlags.SdiSubmission))
        {
            logger.LogInformation("SDI status poll: the module is off, nothing is done");
            return;
        }

        IReadOnlyList<SdiSubmittedDocument> submitted;
        await using (var listing = scopeFactory.CreateAsyncScope())
        {
            if (!listing.ServiceProvider.GetRequiredService<ISdiEInvoiceProvider>().IsConfigured)
            {
                logger.LogInformation("SDI status poll: no provider is configured, nothing is done");
                return;
            }

            submitted = await listing.ServiceProvider.GetRequiredService<IFiscalDocumentSdiService>().SubmittedDocumentsAsync(MaxDocumentsPerRun, ct);
        }

        var failed = 0;
        var recorded = 0;
        foreach (var document in submitted)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IBackgroundTenantScope>().EnterOrg(document.OrgId);
                var status = await scope.ServiceProvider.GetRequiredService<ISdiEInvoiceProvider>().GetStatusAsync(document.TransmissionId, ct);
                if (status.Status == SdiOutcomeKind.Submitted)
                    continue;

                var result = await scope.ServiceProvider.GetRequiredService<IFiscalDocumentSdiService>().RecordOutcomeAsync(document.DocumentId, SdiOutcome.Of(status), ct);
                if (result == SdiRecordResult.Recorded)
                    recorded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                logger.LogError("SDI status of fiscal document {DocumentId} of org {OrgId} not read ({Error}): read again at the next run", document.DocumentId, document.OrgId, ex.GetType().Name);
            }
        }

        logger.LogInformation("SDI status poll: {Asked} documents asked, {Recorded} outcomes recorded, {Failed} not read", submitted.Count, recorded, failed);
        if (failed > 0)
            throw new SdiStatusPollException(failed, submitted.Count);
    }
}

/// <summary>Some documents could not be read by a run of <see cref="SdiStatusPollJob"/> (the others were): the run is a failed run, and the documents are read again at the next one.</summary>
public sealed class SdiStatusPollException(int failed, int asked)
    : Exception($"The SDI status of {failed} of {asked} documents could not be read: they are read again at the next run.");
