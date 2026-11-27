using Casazen.Core.Services;
using Hangfire;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// The import of the documents Octorate issued (FIS-04, NF-84, E06/F03/US02/T01): every 30 minutes, one run per org whose connection is connected, each
/// accommodation from its own last import (with an overlap). Registered only while <c>Features:OctorateConnector</c> and
/// <c>Features:OctorateDocumentImport</c> are both on; the service checks them again and selects no org at all while
/// <c>FiscalDocuments:ImportOctorateDocuments</c> is false (the default since CasaZen is the only issuer, E11/F01/US01/T01). Never two runs at once; an org
/// that fails does not stop the others, and a call Octorate refuses (the static IP whitelist, S7, D24) is logged with ids only and read again at the next run.
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
                // Every accommodation is read from its own last import: a structure that fails keeps its date, whatever the others did.
                await import.ImportAsync(orgId, since: null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("Octorate documents of org {OrgId} not imported ({Error}): read again at the next run", orgId, ex.GetType().Name);
            }
        }
    }
}
