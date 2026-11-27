using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// Keeps the CRM populated (E07/F01/US02/T03, NF-88, NF-20), every 15 minutes: the booker of every booking changed in the last two hours gets
/// its profile, its contacts and its aggregates, and the guests who have no profile yet are linked, 200 a run, until none is left.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>It guarantees the result</b>; the event handler (<c>CrmBookingEventHandler</c>) only shortens the delay. A booking whose event was
/// never recorded (an import, a producer that does not record the lifecycle events) is covered here, within 15 minutes.</item>
/// <item><b>The backfill</b> needs no watermark and no flag: it reads the guests with no <c>GuestCrmProfileId</c> (not erased), oldest first,
/// and ends by itself when every guest is linked. A guest who cannot be linked is logged and tried again by the next run.</item>
/// <item><b>Across the orgs, one at a time</b>: the lists of what to sync are read across every org (<c>IgnoreQueryFilters([TenantQueryFilter])</c>,
/// ids only); each booking or guest is then synced in a scope of its own under its org (<see cref="CrmSyncScopes"/>), which writes every row with
/// the <c>OrgId</c> of that org. A booking that fails is logged with its ids and skipped; the next run retries it.</item>
/// <item><b>While the module is closed</b> (<see cref="CrmProfileSyncGate"/>: <c>Features:CrmMarketing</c> off, or no key of the CRM index,
/// <c>Crm:ContactIndexKey</c>) the run ends at once, with one line in the log, and does not fail. The job is always registered; the gate is asked
/// at every run.</item>
/// <item>Never two runs at once (<see cref="DisableConcurrentExecutionAttribute"/>), and a run starts nothing new after <see cref="RunBudget"/>,
/// so it ends before the next one is due. The logs carry ids and counts, never a name, an address or an error message.</item>
/// </list>
/// </remarks>
[DisableConcurrentExecution(timeoutInSeconds: 60)]
public class CrmProfileSyncJob(
    AppDbContext db,
    IServiceScopeFactory scopeFactory,
    ICrmContactIndex contactIndex,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<CrmProfileSyncJob> logger)
{
    public const string RecurringJobId = "crm-profile-sync";
    public const string Cron = "*/15 * * * *";

    /// <summary>The bookings changed within this time are synced by each run.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(2);

    /// <summary>Guests with no profile linked by one run.</summary>
    public const int BackfillChunkSize = 200;

    /// <summary>A run starts no new booking or guest after this time, so it ends before the next run is due.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(10);

    // The next run comes in a quarter of an hour and does the same: a failed run is not retried.
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (!CrmProfileSyncGate.IsOpen(featureFlags, contactIndex))
        {
            logger.LogInformation("CRM profile sync skipped: Features:CrmMarketing is off or Crm:ContactIndexKey is not configured");
            return;
        }

        var started = timeProvider.GetTimestamp();
        var counts = new Counts();

        // (a) The bookings changed lately, oldest change first.
        var since = timeProvider.GetUtcNow().UtcDateTime - RecentWindow;
        var recent = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.UpdatedAt >= since)
            .OrderBy(b => b.UpdatedAt)
            .ThenBy(b => b.Id)
            .Select(b => new { b.OrgId, b.Id })
            .ToListAsync(cancellationToken);

        foreach (var booking in recent)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeProvider.GetElapsedTime(started) >= RunBudget)
            {
                counts.OutOfTime = true;
                break;
            }

            counts.Bookings++;
            await RunAsync(
                "booking",
                booking.OrgId,
                booking.Id,
                () => CrmSyncScopes.SyncBookingAsync(scopeFactory, booking.OrgId, booking.Id, cancellationToken),
                counts,
                cancellationToken);
        }

        // (b) The guests with no profile, chosen after (a) so the ones it just linked are not read again.
        var unlinked = await db.Guests
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(g => g.GuestCrmProfileId == null && g.DataAnonymizedDate == null && !g.IsDeleted && !g.ErasureRequested)
            .OrderBy(g => g.CreatedAt)
            .ThenBy(g => g.Id)
            .Select(g => new { g.OrgId, g.Id })
            .Take(BackfillChunkSize)
            .ToListAsync(cancellationToken);

        foreach (var guest in unlinked)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeProvider.GetElapsedTime(started) >= RunBudget)
            {
                counts.OutOfTime = true;
                break;
            }

            counts.Guests++;
            await RunAsync(
                "guest",
                guest.OrgId,
                guest.Id,
                () => CrmSyncScopes.SyncGuestAsync(scopeFactory, guest.OrgId, guest.Id, cancellationToken),
                counts,
                cancellationToken);
        }

        logger.LogInformation(
            "CRM profile sync: {Bookings} bookings and {Guests} guests without a profile read, {Synced} synced, {Failed} failed{Stopped}{More}",
            counts.Bookings,
            counts.Guests,
            counts.Synced,
            counts.Failed,
            counts.OutOfTime ? ", stopped by the time budget (the next run goes on)" : string.Empty,
            unlinked.Count == BackfillChunkSize ? ", more guests to link" : string.Empty);
    }

    /// <summary>One booking or guest: a failure is logged with its ids and the kind of error, and the run goes on to the next.</summary>
    private async Task RunAsync(
        string kind,
        Guid orgId,
        Guid id,
        Func<Task<CrmSyncOutcome>> sync,
        Counts counts,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await sync() == CrmSyncOutcome.Synced)
                counts.Synced++;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            counts.Failed++;
            logger.LogWarning(
                "CRM sync of {Kind} {Id} of org {OrgId} failed: {Error}; the next run retries it",
                kind,
                id,
                orgId,
                CrmDatabaseErrors.Describe(ex));
        }
    }

    private sealed class Counts
    {
        public int Bookings { get; set; }

        public int Guests { get; set; }

        public int Synced { get; set; }

        public int Failed { get; set; }

        public bool OutOfTime { get; set; }
    }
}
