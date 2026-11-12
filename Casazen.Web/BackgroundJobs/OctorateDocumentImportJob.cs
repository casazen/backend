using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The import of the documents Octorate issued (FIS-04, NF-84, E06/F03/US02/T01): every 30 minutes, one run per org whose issuer is Octorate and whose
/// connection is connected, from the last import time (with an overlap). Registered only while <c>Features:OctorateConnector</c> and
/// <c>Features:OctorateDocumentImport</c> are both on (the service checks them again). Never two runs at once; an org that fails does not stop the others,
/// and a call Octorate refuses (the static IP whitelist, S7, D24) is logged with ids only and read again at the next run.
/// </summary>
public class OctorateDocumentImportJob(IOctorateDocumentImportService import, ILogger<OctorateDocumentImportJob> logger)
{
    public const string RecurringJobId = "octorate-document-import";

    /// <summary>Every 30 minutes.</summary>
    public const string Cron = "*/30 * * * *";

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(JobLockTimeouts.FrequentSeconds)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var orgId in await import.OrgsToImportAsync(ct))
        {
            try
            {
                var since = await import.LastImportedAtAsync(orgId, ct);
                await import.ImportAsync(orgId, since, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Octorate documents of org {OrgId} not imported ({Error}): read again at the next run", orgId, ex.GetType().Name);
            }
        }
    }
}
