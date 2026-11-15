using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Hangfire;
using Hangfire.Server;

namespace Casazen.Web.BackgroundJobs;

/// <summary>
/// One bulk step of the month-end of an agency for more owners than a request answers for (E13/F01/US03/T01): the posting, the generation, the approval, the publication or the
/// close of a month for every owner of the list, queued by <c>POST api/owner-accounting/periods/{y}/{m}/bulk</c> and read through <c>GET …/bulk/{jobId}</c>.
/// </summary>
/// <remarks>
/// <para>It runs <see cref="IOwnerAccountingService.RunBulkAsync"/> under the tenant of the org it was queued for (every read of the run names the org; the tenant of the scope is a
/// second wall), as the user who asked: the permission of the step was checked by the endpoint, and the audit fields of every operation carry that user. Each owner is its own
/// transaction with its own result; the progress (the results so far) is written to the job after each owner, as a job parameter of Hangfire (<see cref="ProgressParameter"/>),
/// so nothing about a run is stored in the database and a run of another org is never read (<see cref="HangfireOwnerBulkJobs"/>).</para>
/// <para>No automatic retry: the steps are idempotent, so the agency repeats a failed run, which finds every owner done where it is and does the rest. A run that fails as a whole
/// (not one of its owners) is a failed job with <c>owner_bulk_failed</c> (or the code of the rule that stopped it) and the results of the owners done before it. The logs carry
/// ids and counts, never an amount or a name.</para>
/// </remarks>
public sealed class OwnerBulkCloseJob(
    IOwnerAccountingService accounting,
    IBackgroundTenantScope tenantScope,
    ILogger<OwnerBulkCloseJob> logger)
{
    /// <summary>The Hangfire job parameter that holds the progress of a run (<see cref="OwnerBulkProgressState"/> as JSON).</summary>
    public const string ProgressParameter = "owner-bulk-progress";

    /// <param name="orgId">The org the run is for.</param>
    /// <param name="year">The year of the month.</param>
    /// <param name="month">The month.</param>
    /// <param name="action">The step.</param>
    /// <param name="ownerIds">The owners, already resolved when the run was queued (never «every owner» of a later moment).</param>
    /// <param name="userId">Who asked for it.</param>
    /// <param name="context">The Hangfire context of the run (<c>null</c> when it is run by hand, as a test does): where the progress is written.</param>
    /// <param name="cancellationToken">The shutdown of the server.</param>
    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(
        Guid orgId,
        int year,
        int month,
        OwnerBulkAction action,
        Guid[] ownerIds,
        string? userId,
        PerformContext? context,
        CancellationToken cancellationToken)
    {
        tenantScope.EnterOrg(orgId);
        logger.LogInformation("Bulk {Action} of {Year}-{Month} of org {OrgId}: {Owners} owners, run started", action, year, month, orgId, ownerIds.Length);

        IReadOnlyList<OwnerBulkOwnerResult> latest = [];
        Save(context, OwnerBulkRunStatus.Running, latest, null);
        OwnerBulkProgress progress = (done, _) =>
        {
            latest = done.ToList();
            Save(context, OwnerBulkRunStatus.Running, latest, null);
            return Task.CompletedTask;
        };

        try
        {
            var rows = await accounting.RunBulkAsync(orgId, year, month, action, ownerIds, userId, progress, cancellationToken);
            Save(context, OwnerBulkRunStatus.Completed, rows, null);
        }
        catch (OperationCanceledException)
        {
            // The server is shutting down: Hangfire runs the job again, and every owner done is found done.
            throw;
        }
        catch (Exception ex)
        {
            // The run itself failed: what was done stays in the results, the job is a failed one, and the step can be run again.
            var code = ex is DomainException domain ? domain.Code : OwnerAccountingErrors.BulkFailed;
            logger.LogError("Bulk {Action} of {Year}-{Month} of org {OrgId} failed after {Done} owners ({Error}, {Code})", action, year, month, orgId, latest.Count, ex.GetType().Name, code);
            Save(context, OwnerBulkRunStatus.Failed, latest, code);
            throw;
        }
    }

    /// <summary>Writes the progress to the job; without a Hangfire context (a run by hand) nothing is kept.</summary>
    private static void Save(PerformContext? context, OwnerBulkRunStatus status, IReadOnlyList<OwnerBulkOwnerResult> results, string? errorCode)
    {
        if (context?.BackgroundJob?.Id is not { } jobId)
            return;

        context.Connection.SetJobParameter(jobId, ProgressParameter, OwnerBulkJobState.Serialize(new OwnerBulkProgressState(status, results, errorCode)));
    }
}
