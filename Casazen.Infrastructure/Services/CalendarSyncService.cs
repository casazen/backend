using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Http;
using Casazen.Infrastructure.Services.ICal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// iCal sync of the supplier calendars (runbook <c>docs/runbooks/ical.md</c>, "Supplier calendars"). The download and
/// the reading are those of the property feeds: anti-SSRF client (FD-16) and <see cref="ICalImportService"/> (PC-10).
/// The sync always runs in a Hangfire job (SU-15): the first sync after the URL is saved and "sync now" queue
/// <c>IcalSupplierSyncJob.SyncSupplierAsync</c>, the 15-minute job runs <see cref="SyncAllIcalFeedsAsync"/>.
/// <b>What it writes (SP-05):</b> the all-day events close their days in <c>SupplierAvailability</c>, as they always did; the
/// events by the hour become windows (<c>SupplierBusyWindows</c>, kind <see cref="SupplierBusyWindowKind.External"/>, source
/// <see cref="SupplierBusyWindowSource.ICalFeed"/>) that only occupy their own hours, which the slot planner reads.
/// </summary>
public class CalendarSyncService
{
    private readonly AppDbContext _db;
    private readonly ISafeExternalHttpClient _externalHttpClient;
    private readonly ICalImportService _importService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CalendarSyncService> _logger;

    public CalendarSyncService(
        AppDbContext db,
        ISafeExternalHttpClient externalHttpClient,
        ICalImportService importService,
        IServiceScopeFactory scopeFactory,
        ILogger<CalendarSyncService> logger)
    {
        _db = db;
        _externalHttpClient = externalHttpClient;
        _importService = importService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Lock of the availability of one supplier: taken by the sync while it writes (the days and the windows of hours of the
    /// feed) and by every manual change (<c>SupplierService.UpdateAvailabilityAsync</c> and, since SP-03, every write of
    /// <c>SupplierAgendaService</c>), so they never write the same rows at once.
    /// </summary>
    internal static (PostgresAdvisoryLocks.Scope Scope, string Key) AvailabilityLock(Guid orgId) =>
        (PostgresAdvisoryLocks.Scope.SupplierCalendarSync, orgId.ToString("N"));

    /// <summary>
    /// The windows of hours the calendar feed of <paramref name="orgId"/> wrote (SP-05): <see cref="SupplierBusyWindowSource.ICalFeed"/>
    /// only, so the supplier's own blocks and extra openings are out of reach of the sync. <c>SupplierBusyWindows</c> is not
    /// tenant-filtered, the supplier org is an explicit predicate (<c>SupplierAgendaTenancyTests</c> reads the SQL).
    /// </summary>
    internal static IQueryable<SupplierBusyWindow> FeedWindowsOf(AppDbContext db, Guid orgId) =>
        db.SupplierBusyWindows.Where(w => w.OrgId == orgId && w.Source == SupplierBusyWindowSource.ICalFeed);

    /// <summary>
    /// "Sync now": marks the sync <see cref="SupplierCalendarSyncStatus.Syncing"/> and tells the caller to queue the
    /// job. A sync already queued or running is left as it is and nothing is queued: repeated clicks never pile up
    /// downloads (the 15-minute job syncs the feed anyway). Profile null: no supplier profile for the org.
    /// </summary>
    /// <exception cref="DomainRuleException"><see cref="ICalFeedErrorCodes.SupplierNoFeed"/>: no iCal feed configured.</exception>
    public async Task<(SupplierProfile? Profile, bool Queue)> RequestSyncAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await _db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, ct);
        if (profile is null)
            return (null, false);

        if (profile.CalendarSyncType != CalendarSyncType.ICalFeed || string.IsNullOrWhiteSpace(profile.IcalFeedUrl))
            throw new DomainRuleException(ICalFeedErrorCodes.SupplierNoFeed, ICalFeedErrorCodes.SupplierNoFeedMessageKey);

        if (profile.CalendarSyncStatus == SupplierCalendarSyncStatus.Syncing)
            return (profile, false);

        profile.CalendarSyncStatus = SupplierCalendarSyncStatus.Syncing;
        await _db.SaveChangesAsync(ct);
        return (profile, true);
    }

    /// <summary>
    /// Downloads, reads and applies the supplier's iCal feed; every outcome is stored on the profile, so the caller
    /// never sees an exception for a bad feed (PC-10, A9-13).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>A valid calendar is a success even with no event: the days the feed had marked busy and no longer lists
    /// are freed (SU-15), and so are its windows of hours. The days the supplier set by hand
    /// (<see cref="SupplierAvailabilitySource.Manual"/>) and the blocks and extra openings it set by hand
    /// (<see cref="SupplierBusyWindowSource.Manual"/>) are never freed.</item>
    /// <item>A busy day of the feed is written as <see cref="SupplierAvailabilitySource.ICalFeed"/>; a day the supplier
    /// had left open becomes a feed day (the supplier's own calendar says busy); a day the supplier closed stays a
    /// manual closure. Only the events of whole days close days: an event by the hour (10:00-11:00) is a window that
    /// occupies those hours and nothing else (SP-05).</item>
    /// <item>If the feed has events that cannot be read, no feed day and no window is freed at that run: an unreadable event
    /// is not proof that the commitment is gone.</item>
    /// <item>Only a download failure, a document that is not a readable iCalendar or any other failure (e.g. of the
    /// database) is an error: the stable code is stored, the days and the windows are kept.</item>
    /// <item>The days and the windows are written in one transaction under the supplier's advisory lock; a run whose URL was
    /// replaced meanwhile writes nothing (the run of the new URL does).</item>
    /// </list>
    /// Logs name the supplier org id, never the URL (the links carry secret tokens).
    /// </remarks>
    public async Task SyncIcalFeedAsync(Guid orgId, CancellationToken ct = default)
    {
        var feed = await _db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => sp.OrgId == orgId)
            .Select(sp => new { sp.CalendarSyncType, sp.IcalFeedUrl, sp.CalendarSyncStatus })
            .FirstOrDefaultAsync(ct);

        if (feed is null)
            return;

        if (feed.CalendarSyncType != CalendarSyncType.ICalFeed || string.IsNullOrWhiteSpace(feed.IcalFeedUrl))
        {
            // Nothing to download (feed removed after the job was queued): no "syncing" left behind.
            if (feed.CalendarSyncStatus == SupplierCalendarSyncStatus.Syncing)
            {
                var profile = await _db.SupplierProfiles.FirstAsync(sp => sp.OrgId == orgId, ct);
                profile.CalendarSyncStatus = SupplierCalendarSyncStatus.None;
                await _db.SaveChangesAsync(ct);
            }

            return;
        }

        try
        {
            await SyncCoreAsync(orgId, feed.IcalFeedUrl, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Unexpected failure (database, client, ...): the supplier gets its own error state, the days are kept.
            _logger.LogError(ex, "iCal sync failed for supplier {OrgId}", orgId);
            _db.ChangeTracker.Clear(); // never save the rejected changes a second time (A2-12)
            await SaveFailureAsync(orgId, feed.IcalFeedUrl, ICalErrorCodes.SyncFailed, ct);
        }
    }

    private async Task SyncCoreAsync(Guid orgId, string feedUrl, CancellationToken ct)
    {
        string icsContent;
        try
        {
            // Anti-SSRF download (FD-16): https only, public addresses only, size and time limits.
            icsContent = await _externalHttpClient.GetStringAsync(feedUrl, ct);
        }
        catch (ExternalFetchException ex)
        {
            _logger.LogWarning(ex, "iCal download failed for supplier {OrgId} ({Failure})", orgId, ex.Failure);
            await SaveFailureAsync(orgId, feedUrl, ICalErrorCodes.FromFetchFailure(ex.Failure), ct);
            return;
        }

        ICalFeedParseResult parsed;
        SupplierICalBusy busy;
        try
        {
            parsed = _importService.Parse(icsContent);
            busy = _importService.ToSupplierBusy(parsed.Occurrences);
        }
        catch (ICalFormatException ex)
        {
            // Type only: the parser's message can quote the downloaded document.
            _logger.LogWarning(
                "iCal feed of supplier {OrgId} is not a readable iCalendar ({Failure}, {ErrorType})",
                orgId, ex.Failure, ex.InnerException?.GetType().Name);
            await SaveFailureAsync(orgId, feedUrl, ICalErrorCodes.InvalidFormat, ct);
            return;
        }

        if (parsed.SkippedEvents > 0)
        {
            _logger.LogWarning(
                "iCal feed of supplier {OrgId}: {Unreadable} unreadable events and {Unsupported} sub-daily recurrences skipped (first: {FirstError})",
                orgId, parsed.UnreadableEvents, parsed.UnsupportedRecurrences, parsed.FirstUnreadableError);
        }

        if (busy.OverLimit > 0)
        {
            _logger.LogWarning(
                "iCal feed of supplier {OrgId}: {OverLimit} engagements by the hour left out, the supplier keeps the nearest {Limit}",
                orgId, busy.OverLimit, ICalImportService.MaxWindowsPerSupplier);
        }

        var outcome = await ApplyAsync(orgId, feedUrl, busy, keepFeedRows: parsed.UnreadableEvents > 0, ct);
        if (outcome is not { } written)
            return;

        _logger.LogInformation(
            "iCal sync completed for supplier {OrgId}: {BusyDays} busy days, {Marked} marked busy, {Freed} freed; "
            + "{Windows} engagements by the hour, {WindowsAdded} added, {WindowsUpdated} updated, {WindowsRemoved} removed",
            orgId, busy.BusyDays.Count, written.Marked, written.Freed,
            busy.Windows.Count, written.WindowsAdded, written.WindowsUpdated, written.WindowsRemoved);
    }

    /// <summary>What a sync changed: days marked busy and freed, windows of hours added, updated and removed.</summary>
    private readonly record struct SyncOutcome(int Marked, int Freed, int WindowsAdded, int WindowsUpdated, int WindowsRemoved);

    /// <summary>
    /// Writes what the feed occupies, in one transaction under the supplier's advisory lock (<see cref="AvailabilityLock"/>):
    /// the busy days into <c>SupplierAvailability</c> (all-day events, as they always were) and the windows of hours into
    /// <c>SupplierBusyWindows</c> (timed events, SP-05); then marks the sync <see cref="SupplierCalendarSyncStatus.Success"/>.
    /// Returns what changed, or null when the supplier's URL changed during the download (nothing is written).
    /// </summary>
    private async Task<SyncOutcome?> ApplyAsync(
        Guid orgId,
        string feedUrl,
        SupplierICalBusy busy,
        bool keepFeedRows,
        CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(_db, ct, AvailabilityLock(orgId));

        // Read under the lock: a run that held it has committed, READ COMMITTED sees its days.
        var profile = await _db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, ct);
        if (profile is null || profile.IcalFeedUrl != feedUrl)
        {
            _logger.LogInformation("iCal feed of supplier {OrgId} changed during the sync: result discarded", orgId);
            return null;
        }

        var busyDays = busy.BusyDays;
        var busyDayList = busyDays.ToArray();
        var rows = await _db.SupplierAvailability
            .Where(sa => sa.OrgId == orgId
                         && (sa.Source == SupplierAvailabilitySource.ICalFeed || busyDayList.Contains(sa.Date)))
            .ToListAsync(ct);
        var rowsByDate = rows.ToDictionary(r => r.Date);

        var marked = 0;
        foreach (var day in busyDays)
        {
            if (!rowsByDate.TryGetValue(day, out var row))
            {
                _db.SupplierAvailability.Add(new SupplierAvailability
                {
                    OrgId = orgId,
                    Date = day,
                    Available = false,
                    Source = SupplierAvailabilitySource.ICalFeed,
                });
                marked++;
            }
            else if (row.Available)
            {
                // Open day, busy in the supplier's own calendar: the feed takes it (and frees it when the event goes).
                row.Available = false;
                row.Source = SupplierAvailabilitySource.ICalFeed;
                marked++;
            }

            // A day already closed keeps its source: a manual closure stays manual even when the feed agrees.
        }

        var freed = 0;
        if (!keepFeedRows)
        {
            // An empty feed frees every day it had marked (A9-13), never a day the supplier set by hand.
            var released = rows
                .Where(r => r.Source == SupplierAvailabilitySource.ICalFeed && !busyDays.Contains(r.Date))
                .ToList();
            _db.SupplierAvailability.RemoveRange(released);
            freed = released.Count;
        }

        var (windowsAdded, windowsUpdated, windowsRemoved) = await ReplaceWindowsAsync(orgId, busy.Windows, keepFeedRows, ct);

        profile.CalendarSyncStatus = SupplierCalendarSyncStatus.Success;
        profile.CalendarSyncError = null;
        profile.CalendarLastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return new SyncOutcome(marked, freed, windowsAdded, windowsUpdated, windowsRemoved);
    }

    /// <summary>
    /// Makes the windows of hours of the feed (<c>SupplierBusyWindows</c>, <see cref="SupplierBusyWindowSource.ICalFeed"/>) the
    /// ones in <paramref name="wanted"/>, by key (UID + start): a window that is already there is kept (its end and label are
    /// updated if they changed), a new one is added, one the feed no longer lists is removed. The supplier's own blocks and
    /// extra openings (<see cref="SupplierBusyWindowSource.Manual"/>) are never read nor touched. Runs inside the transaction
    /// of <see cref="ApplyAsync"/>, under the lock, so no one else writes these rows meanwhile. Idempotent: the same feed
    /// again changes nothing.
    /// </summary>
    /// <remarks>
    /// With <paramref name="keepFeedRows"/> (the feed has events that could not be read) nothing is removed at this run, like
    /// the days of the feed: an event the reader cannot understand is not proof that the commitment is gone. A row that
    /// stays is never both removed and added back: the added keys are the ones that were not there, the removed ones are
    /// the ones that were and are no longer listed, so the unique index on the key is never hit.
    /// </remarks>
    private async Task<(int Added, int Updated, int Removed)> ReplaceWindowsAsync(
        Guid orgId,
        IReadOnlyList<ParsedSupplierWindow> wanted,
        bool keepFeedRows,
        CancellationToken ct)
    {
        var existing = await FeedWindowsOf(_db, orgId).ToListAsync(ct);

        // Equal after the round trip: the instants of a feed are whole seconds and the column keeps microseconds.
        var byKey = new Dictionary<(string Uid, DateTime StartUtc), SupplierBusyWindow>();
        var leftovers = new List<SupplierBusyWindow>();
        foreach (var row in existing)
        {
            if (!byKey.TryAdd((row.ExternalUid ?? string.Empty, row.StartUtc), row))
                leftovers.Add(row); // twice the same key (no unique index on this provider): one is enough
        }

        var added = 0;
        var updated = 0;
        foreach (var window in wanted)
        {
            if (byKey.Remove((window.ExternalUid, window.StartUtc), out var row))
            {
                if (row.EndUtc != window.EndUtc || row.Label != window.Label)
                {
                    row.EndUtc = window.EndUtc;
                    row.Label = window.Label;
                    updated++;
                }

                continue;
            }

            _db.SupplierBusyWindows.Add(new SupplierBusyWindow
            {
                OrgId = orgId,
                StartUtc = window.StartUtc,
                EndUtc = window.EndUtc,
                Kind = SupplierBusyWindowKind.External,
                Source = SupplierBusyWindowSource.ICalFeed,
                Label = window.Label,
                ExternalUid = window.ExternalUid,
            });
            added++;
        }

        var removed = 0;
        if (!keepFeedRows)
        {
            leftovers.AddRange(byKey.Values);
            _db.RemoveRange(leftovers);
            removed = leftovers.Count;
        }

        return (added, updated, removed);
    }

    // Stores the stable error code, never the exception message (FD-16, A4-10: the message told the supplier
    // what the server could reach). The context holds no pending change here. A profile whose URL was replaced meanwhile
    // is left to the run of the new URL.
    private async Task SaveFailureAsync(Guid orgId, string feedUrl, string errorCode, CancellationToken ct)
    {
        var profile = await _db.SupplierProfiles.FirstOrDefaultAsync(sp => sp.OrgId == orgId, ct);
        if (profile is null || profile.IcalFeedUrl != feedUrl)
            return;

        profile.CalendarSyncStatus = SupplierCalendarSyncStatus.Failure;
        profile.CalendarSyncError = errorCode;
        profile.CalendarLastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Syncs the feed of every active supplier and of every supplier still in the activation wizard (pending), least
    /// recently synced first: the days of a new supplier are ready when the profile is activated (SU-15, A4-11);
    /// suspended suppliers are skipped. Each supplier runs
    /// in its own DI scope (its own <see cref="AppDbContext"/>) and its own try/catch: a supplier whose sync fails, even
    /// unexpectedly, never stops the others. Only cancellation stops the batch. Running it again with the same feeds
    /// writes nothing new (idempotent).
    /// </summary>
    public async Task SyncAllIcalFeedsAsync(CancellationToken ct = default)
    {
        var orgIds = await _db.SupplierProfiles
            .AsNoTracking()
            .Where(sp => (sp.Status == SupplierStatus.Active || sp.Status == SupplierStatus.Pending)
                         && sp.CalendarSyncType == CalendarSyncType.ICalFeed
                         && !string.IsNullOrWhiteSpace(sp.IcalFeedUrl))
            .OrderBy(sp => sp.CalendarLastSyncAt.HasValue)
            .ThenBy(sp => sp.CalendarLastSyncAt)
            .Select(sp => sp.OrgId)
            .ToListAsync(ct);

        var failed = 0;
        foreach (var orgId in orgIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider
                    .GetRequiredService<CalendarSyncService>()
                    .SyncIcalFeedAsync(orgId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed++;
                _logger.LogError(ex, "iCal sync of supplier {OrgId} failed; continuing with the next supplier", orgId);
            }
        }

        _logger.LogInformation(
            "Batch supplier iCal sync completed for {Count} suppliers ({Failed} failed unexpectedly)", orgIds.Count, failed);
    }
}
