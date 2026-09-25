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
    /// Lock of the availability days of one supplier: taken by the sync while it writes and by the supplier's manual
    /// changes (<c>SupplierService.UpdateAvailabilityAsync</c>), so they never write the same days at once.
    /// </summary>
    internal static (PostgresAdvisoryLocks.Scope Scope, string Key) AvailabilityLock(Guid orgId) =>
        (PostgresAdvisoryLocks.Scope.SupplierCalendarSync, orgId.ToString("N"));

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
    /// are freed (SU-15). The days the supplier set by hand (<see cref="SupplierAvailabilitySource.Manual"/>) are never
    /// freed.</item>
    /// <item>A busy day of the feed is written as <see cref="SupplierAvailabilitySource.ICalFeed"/>; a day the supplier
    /// had left open becomes a feed day (the supplier's own calendar says busy); a day the supplier closed stays a
    /// manual closure.</item>
    /// <item>If the feed has events that cannot be read, no feed day is freed at that run: an unreadable event is not
    /// proof that the commitment is gone.</item>
    /// <item>Only a download failure, a document that is not a readable iCalendar or any other failure (e.g. of the
    /// database) is an error: the stable code is stored, the days are kept.</item>
    /// <item>The days are written in one transaction under the supplier's advisory lock; a run whose URL was replaced
    /// meanwhile writes nothing (the run of the new URL does).</item>
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
        IReadOnlySet<DateOnly> busyDays;
        try
        {
            parsed = _importService.Parse(icsContent);
            busyDays = ICalImportService.ToBusyDays(parsed.Occurrences);
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

        var outcome = await ApplyBusyDaysAsync(orgId, feedUrl, busyDays, keepFeedDays: parsed.UnreadableEvents > 0, ct);
        if (outcome is not { } written)
            return;

        _logger.LogInformation(
            "iCal sync completed for supplier {OrgId}: {BusyDays} busy days, {Marked} marked busy, {Freed} freed",
            orgId, busyDays.Count, written.Marked, written.Freed);
    }

    /// <summary>
    /// Writes the busy days of the feed and marks the sync <see cref="SupplierCalendarSyncStatus.Success"/>. Returns the
    /// days marked busy and freed, or null when the supplier's URL changed during the download (nothing is written).
    /// </summary>
    private async Task<(int Marked, int Freed)?> ApplyBusyDaysAsync(
        Guid orgId,
        string feedUrl,
        IReadOnlySet<DateOnly> busyDays,
        bool keepFeedDays,
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

        var busy = busyDays.ToArray();
        var rows = await _db.SupplierAvailability
            .Where(sa => sa.OrgId == orgId
                         && (sa.Source == SupplierAvailabilitySource.ICalFeed || busy.Contains(sa.Date)))
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
        if (!keepFeedDays)
        {
            // An empty feed frees every day it had marked (A9-13), never a day the supplier set by hand.
            var released = rows
                .Where(r => r.Source == SupplierAvailabilitySource.ICalFeed && !busyDays.Contains(r.Date))
                .ToList();
            _db.SupplierAvailability.RemoveRange(released);
            freed = released.Count;
        }

        profile.CalendarSyncStatus = SupplierCalendarSyncStatus.Success;
        profile.CalendarSyncError = null;
        profile.CalendarLastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return (marked, freed);
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
