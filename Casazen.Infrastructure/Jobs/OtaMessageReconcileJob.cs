using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The safety net of the webhook for the OTA conversations, every 10 minutes (E05/F01/US01/T03, ADR-032, NF-62, OCT-09; the 10 minutes are the
/// request of the product owner of 2026-11-04 and supersede the 15-minute poll of ADR-032-R4, an amendment of the ADR is pending): for every org
/// whose connection to the channel manager is connected it reads the messages of the last 24 hours and the threads they name
/// (<see cref="IMessageSyncService.ReconcileRecentAsync"/>), then imports the history of the confirmed listings that have none yet. Behind
/// <c>Features:OctorateConnector</c> (the registration removes it while the flag is off, and a run with the flag off does nothing).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant.</b> A system read over every org to find the orgs (explicit <c>OrgId</c> predicates, no tenant filter); each org is then read for
/// itself under <see cref="IOctorateOrgScope.Use"/>, which is what lets the client (no org parameter) act for it.</item>
/// <item><b>The initial import.</b> The confirmed listings (the rows of <see cref="IChannelMappingService.ListAsync"/>) whose checkpoints do not say the
/// import finished are imported within a budget of <see cref="ImportBudget"/> for the whole run, a slice of <see cref="ImportSlice"/> for each, the
/// listing that was worked on longest ago first, so a listing with a long history never keeps the others waiting. The position is saved after each
/// page: the next run resumes from it after the budget, a rate limit or a crash, and no message is stored twice. A listing whose chat is not in its
/// license is left alone for an hour.</item>
/// <item><b>Failures.</b> An <see cref="OctorateRateLimitException"/> ends the run of that org (the checkpoints continue it next time); any other
/// failure of an org was stored by the service as the code on the checkpoints of its listing (<c>LastError</c>) and the next org still runs. No retry by
/// Hangfire: the next run, ten minutes later, reads the same window.</item>
/// <item><b>Logs.</b> Ids, counts and the type of an error, never a text, a name or an address.</item>
/// <item>The lock waits <see cref="LockTimeoutSeconds"/>, the value of <c>JobLockTimeouts.FrequentSeconds</c>, far less than the interval (a test keeps the
/// two equal; the jobs live in the infrastructure project, which cannot see that class).</item>
/// </list>
/// </remarks>
public class OtaMessageReconcileJob(
    AppDbContext db,
    IMessageSyncService sync,
    IChannelMappingService mappings,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<OtaMessageReconcileJob> logger)
{
    public const string RecurringJobId = "ota-message-reconcile";

    /// <summary>Every 10 minutes (UTC).</summary>
    public const string Cron = "*/10 * * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.FrequentSeconds</c>.</summary>
    public const int LockTimeoutSeconds = 60;

    /// <summary>The time the initial imports of a whole run may take.</summary>
    public static readonly TimeSpan ImportBudget = TimeSpan.FromMinutes(4);

    /// <summary>The time one listing may take of the budget, so that the others are reached in the same run.</summary>
    public static readonly TimeSpan ImportSlice = TimeSpan.FromMinutes(1);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Octorate message reconciliation: the connector is off, nothing done");
            return;
        }

        var orgIds = await OtaMessageRuns.ListConnectedOrgsAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var totals = new RunTotals();
        var stopped = new HashSet<Guid>();

        foreach (var orgId in orgIds)
        {
            try
            {
                SyncResult result;
                using (orgScope.Use(orgId))
                    result = await sync.ReconcileRecentAsync(orgId, cancellationToken);

                totals.Add(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OctorateRateLimitException)
            {
                // The org used its calls: what is left is continued by the next run.
                stopped.Add(orgId);
                totals.RateLimited++;
                logger.LogWarning("Octorate message reconciliation of org {OrgId} stopped: its calls to Octorate are used up, the next run continues", orgId);
            }
            catch (Exception ex)
            {
                totals.Failed++;
                logger.LogWarning(
                    "Octorate message reconciliation of org {OrgId} failed ({Error}): the next run reads it again", orgId, ErrorOf(ex));
            }
        }

        await ImportHistoriesAsync(orgIds.Where(o => !stopped.Contains(o)).ToList(), stopped, totals, cancellationToken);

        logger.LogInformation(
            "Octorate message reconciliation: {Orgs} orgs, {Threads} threads read, {Added} messages added, {Unassigned} threads with no property, {Imported} listings imported (not complete: {Incomplete}), {RateLimited} orgs stopped by the rate limit, {Failed} failed",
            orgIds.Count,
            totals.Threads,
            totals.Added,
            totals.Unassigned,
            totals.Imported,
            totals.ImportIncomplete,
            totals.RateLimited,
            totals.Failed);
    }

    private async Task ImportHistoriesAsync(IReadOnlyList<Guid> orgIds, HashSet<Guid> stopped, RunTotals totals, CancellationToken ct)
    {
        var pending = new List<PendingImport>();
        foreach (var orgId in orgIds)
            pending.AddRange(await PendingImportsAsync(orgId, ct));

        if (pending.Count == 0)
            return;

        var started = timeProvider.GetTimestamp();
        foreach (var item in pending.OrderBy(p => p.WorkedOnAt ?? DateTime.MinValue).ThenBy(p => p.AccommodationId, StringComparer.Ordinal))
        {
            var left = ImportBudget - timeProvider.GetElapsedTime(started);
            if (left <= TimeSpan.Zero)
                break;

            if (stopped.Contains(item.OrgId))
                continue;

            try
            {
                SyncResult result;
                using (orgScope.Use(item.OrgId))
                    result = await sync.ImportPropertyAsync(item.PropertyId, ct, left < ImportSlice ? left : ImportSlice);

                totals.Imported++;
                if (!result.Complete)
                    totals.ImportIncomplete++;

                totals.Add(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OctorateRateLimitException)
            {
                stopped.Add(item.OrgId);
                totals.RateLimited++;
                logger.LogWarning("Octorate message import of org {OrgId} stopped: its calls to Octorate are used up, the next run continues", item.OrgId);
            }
            catch (Exception ex)
            {
                totals.Failed++;
                logger.LogWarning(
                    "Octorate message import of listing {AccommodationId} of org {OrgId} failed ({Error}): the next run continues from its position",
                    item.AccommodationId,
                    item.OrgId,
                    ErrorOf(ex));
            }
        }
    }

    /// <summary>
    /// The confirmed listings of the org whose import of the history did not finish, except the ones whose chat is not in the license and were asked
    /// less than <see cref="MessageSyncService.LicenseProbeInterval"/> ago.
    /// </summary>
    private async Task<IReadOnlyList<PendingImport>> PendingImportsAsync(Guid orgId, CancellationToken ct)
    {
        var rows = await mappings.ListAsync(orgId, ct);
        var confirmed = rows
            .Where(r => r.Status == OctorateMappingStatus.Confirmed && r.Property is not null)
            .ToList();
        if (confirmed.Count == 0)
            return [];

        var states = await db.OtaMessageSyncStates
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(s => s.OrgId == orgId)
            .ToDictionaryAsync(s => s.OctorateAccommodationId, StringComparer.Ordinal, ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var pending = new List<PendingImport>();
        foreach (var row in confirmed)
        {
            states.TryGetValue(row.OctorateAccommodationId, out var state);
            if (state?.InitialImportCompletedAt is not null)
                continue;

            if (state is { LastError: OtaMessageSyncErrorCodes.ChatNotLicensed } && state.UpdatedAt > now - MessageSyncService.LicenseProbeInterval)
                continue;

            pending.Add(new PendingImport(orgId, row.OctorateAccommodationId, row.Property!.Id, state?.UpdatedAt));
        }

        return pending;
    }

    /// <summary>The kind of an error for a log line: the code of an Octorate error, else the type of the exception.</summary>
    private static string ErrorOf(Exception ex) => ex is OctorateException octorate ? octorate.Code : ex.GetType().Name;

    /// <summary>A listing to import: its org, itself, its property, and when it was last worked on (<c>null</c>: never).</summary>
    private sealed record PendingImport(Guid OrgId, string AccommodationId, Guid PropertyId, DateTime? WorkedOnAt);

    /// <summary>The counts of a whole run, for its log line.</summary>
    private sealed class RunTotals
    {
        public int Threads { get; private set; }

        public int Added { get; private set; }

        public int Unassigned { get; private set; }

        public int Imported { get; set; }

        public int ImportIncomplete { get; set; }

        public int RateLimited { get; set; }

        public int Failed { get; set; }

        public void Add(SyncResult result)
        {
            Threads += result.ThreadsSeen;
            Added += result.MessagesAdded;
            Unassigned += result.Unassigned;
        }
    }
}
