using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Jobs;

/// <summary>
/// The calendar of the Octorate listings, read into CasaZen every 15 minutes (E02/F02/US01/T03, ADR-002 amended 2026-11-03, NF-36): the nights a
/// listing cannot sell become calendar blocks of the property, so the booking site, the host calendar and the checks that refuse a booking on
/// taken dates know them, and, after the first successful read, the iCal feeds of the channels the listing has are handed over to Octorate
/// (<see cref="ICalMigrationService"/>). Behind <c>Features:OctorateConnector</c> (the registration removes it while the flag is off).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Inbound only (PO-16, one writer per direction).</b> This job reads Octorate and never writes to it: the push of the manual blocks, the
/// prices and the restrictions is built by E08/F05/US02.</item>
/// <item><b>What becomes a block.</b> The nights Octorate holds closed (<see cref="OctorateCalendarDay.IsClosed"/>: stopped, or no unit on a day that is
/// on sale) from today to <see cref="HorizonDays"/> days ahead, in the calendar of the property, that nothing else in CasaZen takes already: a
/// reservation of a channel arrives as a booking (the import of the bookings), a closure made in CasaZen is a manual block, a channel not yet
/// handed over still comes as iCal. Consecutive nights make one block of source <see cref="CalendarBlockSource.Octorate"/>, key
/// <c>(PropertyId, ExternalUid)</c> with the date of its first night, no feed, no currency (it reads as the currency of the property), a neutral
/// note for the host, never exported (the iCal export writes the manual blocks only: no echo).</item>
/// <item><b>Days Octorate holds but nobody opened</b> (availability 0 and price 0, the state of a calendar never filled, or that cannot be written
/// for lack of a licence) close nothing: no channel can sell them, and reading them as closed would shut the property on the booking site. A
/// calendar with <i>no</i> opened day at all (<see cref="OctorateCalendarDay.IsOpened"/>) is not a live calendar of the room: nothing is read from
/// it, the blocks stay as they were and the iCal feeds keep being imported, because it does not show that Octorate has taken over.</item>
/// <item><b>Fail closed.</b> An open day needs an explicit signal: the units available or the stop flag (<see cref="OctorateCalendarDay.HasSignal"/>).
/// An answer in which no day has either (days that hold only a date, or a date and a price) is not understood: it is not "everything is open", it
/// changes nothing and deprecates nothing. Nor does an answer that does not reach both ends of the window that was asked for (its first and its last
/// day, <see cref="OctorateClosedNights.Covers"/>): a night that is not in the answer is not known to be open. The <c>page</c> of the answer is not
/// followed; one that stops short because of it is such an answer.</item>
/// <item><b>Replaced, not accumulated.</b> Each run rewrites the Octorate blocks of the property in one transaction, under the iCal lock of the
/// property: a night Octorate reopened, or that a booking or a manual block now takes, leaves its block; one that is closed again comes back. A row
/// that did not change is not written.</item>
/// <item><b>Restrictions.</b> The minimum and maximum stay and the closed arrival and departure of each day are read by the client
/// (<see cref="OctorateCalendarDay"/>) but stored nowhere: they close no night, CasaZen is the sole writer of the restrictions (PO-16) and has no
/// column for the ones of the channel manager. The pricing and rule tasks of E08/F05 are their consumers.</item>
/// <item><b>A read that cannot be trusted changes nothing.</b> An answer without a day of the room, one that stops short of the window or has no day
/// with a signal, a failed call or an error of the database leaves the blocks as they were and the iCal feeds imported: nothing is deprecated on a
/// guess (<see cref="ICalMigrationService"/>).</item>
/// <item><b>Tenant.</b> A system read over every org; each property is read for its own org under <see cref="IOctorateOrgScope.Use"/> (the client
/// takes no org), and every query names the org. A property that fails does not stop the others; an org whose connection is refused, in the wrong
/// environment or over its rate limit stops the org until the next run. No retry by Hangfire: the next run reads the same window.</item>
/// <item><b>Properties no longer mapped.</b> The Octorate blocks of a property with no confirmed mapping at all (the host rejected or changed it) are
/// removed at the end of the run, so a closure that no longer belongs to the property cannot stay on it. A property whose connection is only
/// disconnected or in error keeps them: its nights stay closed until the connection is back.</item>
/// <item><b>The hand-over is not one way.</b> A property that is not read any more, because its mapping is not confirmed or its connection is not
/// connected, gets its deprecated iCal feeds back at the end of the run (<see cref="ICalMigrationService.ReinstateFeedsAsync"/>): they are imported
/// again, the only source left for its calendar. They are deprecated again by the first read that works after the listing is back. A read that
/// only failed changes nothing.</item>
/// <item>The lock waits <see cref="LockTimeoutSeconds"/>, the value of <c>JobLockTimeouts.FrequentSeconds</c> (a test keeps the two equal).</item>
/// </list>
/// </remarks>
public class CalendarSyncJob(
    AppDbContext db,
    IOctorateApiClient octorate,
    IOctorateOrgScope orgScope,
    ICalMigrationService migration,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<CalendarSyncJob> logger)
{
    public const string RecurringJobId = "CalendarSync";

    /// <summary>Every 15 minutes (UTC), like <c>property-ical-sync</c> (ADR-002-R2, NF-36).</summary>
    public const string Cron = "*/15 * * * *";

    /// <summary>The seconds a run waits for the previous one: <c>JobLockTimeouts.FrequentSeconds</c>.</summary>
    public const int LockTimeoutSeconds = 60;

    /// <summary>How many days after today the calendar is read (today included, so a window of one year and a day).</summary>
    public const int HorizonDays = 365;

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Delete)]
    [DisableConcurrentExecution(timeoutInSeconds: LockTimeoutSeconds)]
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var properties = await OctorateBookingRuns.ListMappedPropertiesAsync(db, OctorateEnvironments.Of(hostEnvironment), cancellationToken);
        var totals = new RunTotals();

        foreach (var org in properties.GroupBy(p => p.OrgId))
        {
            using (orgScope.Use(org.Key))
            {
                foreach (var property in org)
                {
                    if (!await RunOneAsync(property, totals, cancellationToken))
                        break;
                }
            }
        }

        totals.Released = await RemoveBlocksOfUnmappedPropertiesAsync(cancellationToken);

        // The hand-over is not one way: a property whose calendar is not read any more (mapping not confirmed, connection not connected) gets its
        // feeds back, because nothing else would keep its calendar up to date. A property whose read failed this time is still in the list.
        var feedsBack = await migration.ReinstateFeedsAsync([.. properties.Select(p => p.PropertyId)], cancellationToken);
        totals.FeedsReinstated = feedsBack.Count;

        logger.LogInformation(
            "Octorate calendar sync: {Properties} properties read, {Closed} closed nights in {Blocks} blocks ({Added} added, {Changed} changed, {Removed} removed), {Feeds} iCal feeds deprecated, {Unreadable} answers not used (not understood, stops short of the window, or no day opened), {Failed} failed, {Released} blocks of unmapped properties removed, {Reinstated} iCal feeds given back to the import",
            totals.Properties,
            totals.ClosedNights,
            totals.Blocks,
            totals.Added,
            totals.Changed,
            totals.Removed,
            totals.FeedsDeprecated,
            totals.Unreadable,
            totals.Failed,
            totals.Released,
            totals.FeedsReinstated);
    }

    // One property: a failure is logged (ids and the type of the error) and does not stop the other properties; false when the org cannot be
    // read at all (its connection is refused, the environment is wrong, its rate limit is spent) and its other properties would fail alike.
    private async Task<bool> RunOneAsync(OctorateBookingRuns.MappedProperty property, RunTotals totals, CancellationToken ct)
    {
        try
        {
            totals.Add(await SyncPropertyAsync(property, ct));
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is OctorateAuthException or OctorateWrongEnvironmentException or OctorateRateLimitException)
        {
            db.ChangeTracker.Clear();
            totals.Failed++;
            logger.LogWarning(
                "Octorate calendar read of org {OrgId} stopped ({Error}): the other properties of the org are left for the next run",
                property.OrgId,
                ex.GetType().Name);
            return false;
        }
        catch (Exception ex)
        {
            // Nothing the failed run held is kept: the context is clean for the next property.
            db.ChangeTracker.Clear();
            totals.Failed++;
            logger.LogWarning(
                "Octorate calendar of property {PropertyId} of org {OrgId} failed ({Error}): the next run reads it again",
                property.PropertyId,
                property.OrgId,
                ex.GetType().Name);
            return true;
        }
    }

    private async Task<PropertyOutcome> SyncPropertyAsync(OctorateBookingRuns.MappedProperty target, CancellationToken ct)
    {
        // A deleted property (PC-05) is no longer synced; the soft-delete filter of the model hides it, the tenant filter is ignored (a system read).
        var timezone = await db.Properties
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(p => p.Id == target.PropertyId && p.OrgId == target.OrgId)
            .Select(p => p.Timezone)
            .FirstOrDefaultAsync(ct);
        if (timezone is null)
            return PropertyOutcome.Skipped;

        var now = timeProvider.GetUtcNow();
        var from = LocalToday(now, timezone);
        var to = from.AddDays(HorizonDays);

        var read = await octorate.GetCalendarAsync(target.AccommodationId, [target.ProductId], from, to, ct);
        var days = OctorateClosedNights.DaysOf(read, target.ProductId);
        if (days.Count == 0)
        {
            logger.LogWarning(
                "Octorate calendar of property {PropertyId}: the answer has no day of the room, nothing was changed and no iCal feed was deprecated",
                target.PropertyId);
            return PropertyOutcome.Unreadable;
        }

        // The answer must reach both ends of the window that was asked for (its first and its last day). One that stops short (days left out, a
        // second page that is not followed) says nothing of the nights after it, and a night that is not in the answer must not read as open: its
        // block would be removed and the feeds handed over on a guess.
        if (!OctorateClosedNights.Covers(days, from, to))
        {
            logger.LogWarning(
                "Octorate calendar of property {PropertyId}: the answer does not reach both ends of the window ({From} to {To}), nothing was changed and no iCal feed was deprecated",
                target.PropertyId,
                from,
                to);
            return PropertyOutcome.Unreadable;
        }

        // A day that says neither how many units it has nor whether it is stopped (only its date, or its date and a price) is no reading of the
        // calendar. An answer made of such days cannot tell an open night from a closed one: it must not read as "everything is open" and
        // remove the blocks, so nothing is changed and no feed is deprecated on it.
        if (!days.Any(d => d.HasSignal))
        {
            logger.LogWarning(
                "Octorate calendar of property {PropertyId}: no day of the answer says its availability or whether it is stopped, nothing was changed and no iCal feed was deprecated",
                target.PropertyId);
            return PropertyOutcome.Unreadable;
        }

        // A calendar nobody opened (no unit, no price, nothing stopped, on any day) is not a live calendar of the room: it says nothing about the
        // nights and proves nothing about Octorate having taken over, so the blocks stay as they were and the iCal feeds keep being imported.
        if (!days.Any(d => d.IsOpened))
        {
            logger.LogWarning(
                "Octorate calendar of property {PropertyId}: no day of the room has been opened, nothing was changed and no iCal feed was deprecated",
                target.PropertyId);
            return PropertyOutcome.NotOpened;
        }

        var change = await ReplaceBlocksAsync(target, days, from, to, now.UtcDateTime, ct);

        // The read worked and the blocks are saved: the calendar of the listing now stands for the iCal feeds of its channels (ADR-002-R11, R14).
        var feeds = await migration.DeprecateCoveredFeedsAsync(target.PropertyId, ct);
        db.ChangeTracker.Clear();

        var level = change.IsUnchanged && feeds.Count == 0 ? LogLevel.Debug : LogLevel.Information;
        logger.Log(
            level,
            "Octorate calendar of property {PropertyId} read: {Closed} closed nights in {Blocks} blocks ({Added} added, {Changed} changed, {Removed} removed), {Feeds} iCal feeds deprecated",
            target.PropertyId,
            change.ClosedNights,
            change.Blocks,
            change.Added,
            change.Changed,
            change.Removed,
            feeds.Count);

        return new PropertyOutcome(PropertyState.Synced, change, feeds.Count);
    }

    /// <summary>
    /// Replaces the Octorate blocks of the property with the ones the closed nights make, in one transaction under the iCal lock of the property
    /// (the manual blocks and the feed syncs of the property take the same lock): the rows with the date of a first night that is still closed are
    /// kept and, when their end moved, updated; the others are removed; the new ones are added.
    /// </summary>
    private async Task<BlockChange> ReplaceBlocksAsync(
        OctorateBookingRuns.MappedProperty target,
        IReadOnlyList<OctorateCalendarDay> days,
        DateOnly from,
        DateOnly to,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.PropertyICalSync, target.PropertyId.ToString()));

        // Read under the lock: a booking or a manual block that was being written has been committed, READ COMMITTED sees it.
        var taken = await NightsTakenElsewhereAsync(target, from, to, ct);
        var nights = OctorateClosedNights.Of(days, from, to, taken);
        var runs = OctorateClosedNights.Runs(nights);

        var existing = await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => b.OrgId == target.OrgId && b.PropertyId == target.PropertyId && b.Source == CalendarBlockSource.Octorate)
            .ToListAsync(ct);
        var byUid = existing
            .Where(b => b.ExternalUid is not null)
            .GroupBy(b => b.ExternalUid!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var keep = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        var changed = 0;
        foreach (var run in runs)
        {
            var uid = OctorateClosedNights.UidOf(run.FirstNight);
            keep.Add(uid);
            var start = StartOf(run.FirstNight);
            var end = StartOf(run.CheckOut);

            if (byUid.TryGetValue(uid, out var row))
            {
                // A row that did not change is not written (LastSyncedAt is the time of the last change).
                if (row.StartUtc == start && row.EndUtc == end && row.Summary == OctorateClosedNights.BlockSummary)
                    continue;

                row.StartUtc = start;
                row.EndUtc = end;
                row.Summary = OctorateClosedNights.BlockSummary;
                row.LastSyncedAt = nowUtc;
                changed++;
                continue;
            }

            db.CalendarBlocks.Add(new CalendarBlock
            {
                PropertyId = target.PropertyId,
                OrgId = target.OrgId,
                Source = CalendarBlockSource.Octorate,
                ExternalUid = uid,
                StartUtc = start,
                EndUtc = end,
                Summary = OctorateClosedNights.BlockSummary,
                LastSyncedAt = nowUtc,
            });
            added++;
        }

        var stale = existing.Where(b => b.ExternalUid is null || !keep.Contains(b.ExternalUid)).ToList();
        db.CalendarBlocks.RemoveRange(stale);

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return new BlockChange(nights.Count, runs.Count, added, changed, stale.Count);
    }

    /// <summary>
    /// The nights of the window that something else in CasaZen takes already: the bookings that are not cancelled (the reservations of the
    /// channels among them, and the ones CasaZen sent to Octorate) and the blocks of every other source (manual, iCal). A night the host closed
    /// by hand and CasaZen sent to Octorate does not come back as a second block.
    /// </summary>
    private async Task<HashSet<DateOnly>> NightsTakenElsewhereAsync(
        OctorateBookingRuns.MappedProperty target,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var windowStart = StartOf(from);
        var windowEnd = StartOf(to.AddDays(1));

        // Strict, like the final check before an insert: every booking that is not cancelled takes its dates.
        var stays = await db.Bookings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == target.OrgId)
            .Where(PropertyOccupancy.BookingTakesNightIn(target.PropertyId, windowStart, windowEnd))
            .Where(CheckoutHolds.OccupiesDates(null))
            .Select(b => new { b.CheckInDate, b.CheckOutDate })
            .ToListAsync(ct);

        var blocks = await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(b => b.OrgId == target.OrgId && b.Source != CalendarBlockSource.Octorate)
            .Where(PropertyOccupancy.BlockTakesNightIn(target.PropertyId, windowStart, windowEnd))
            .Select(b => new { b.StartUtc, b.EndUtc })
            .ToListAsync(ct);

        var taken = new HashSet<DateOnly>();
        foreach (var stay in stays)
            taken.UnionWith(OctorateClosedNights.NightsIn(stay.CheckInDate, stay.CheckOutDate, from, to));
        foreach (var block in blocks)
            taken.UnionWith(OctorateClosedNights.NightsIn(block.StartUtc, block.EndUtc, from, to));

        return taken;
    }

    /// <summary>
    /// Removes the Octorate blocks of the properties that have no confirmed mapping at all: the host rejected the listing or mapped the property
    /// again, so the closures of that listing no longer belong to it, and the host cannot remove them by hand (they are not manual blocks). A
    /// property whose mapping is confirmed keeps them, whatever the state of the connection (a disconnected or failing connection must not reopen
    /// nights the channel manager holds closed).
    /// </summary>
    private async Task<int> RemoveBlocksOfUnmappedPropertiesAsync(CancellationToken ct)
    {
        var mapped = await db.OctorateListingMappings
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(m => m.Status == OctorateMappingStatus.Confirmed && m.PropertyId != null)
            .Select(m => m.PropertyId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var orphans = await db.CalendarBlocks
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .Where(b => b.Source == CalendarBlockSource.Octorate && !mapped.Contains(b.PropertyId))
            .ToListAsync(ct);
        if (orphans.Count == 0)
            return 0;

        db.CalendarBlocks.RemoveRange(orphans);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        logger.LogInformation(
            "Octorate blocks of {Properties} properties with no confirmed mapping removed ({Blocks} blocks)",
            orphans.Select(b => b.PropertyId).Distinct().Count(),
            orphans.Count);
        return orphans.Count;
    }

    /// <summary>Today on the clock of the property (NF-36: dates in the timezone of the property); Europe/Rome when the zone is not one the server knows.</summary>
    private static DateOnly LocalToday(DateTimeOffset now, string timezoneId)
    {
        var zone = !string.IsNullOrWhiteSpace(timezoneId) && TimezoneHelper.IsValidTimezone(timezoneId) ? timezoneId : RomeCalendar.TimeZoneId;
        return DateOnly.FromDateTime(TimezoneHelper.ConvertUtcToLocal(now.UtcDateTime, zone));
    }

    /// <summary>The storage form of a night: midnight UTC of its date.</summary>
    private static DateTime StartOf(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    private enum PropertyState
    {
        Synced,
        Skipped,
        Unreadable,
        NotOpened,
    }

    private sealed record BlockChange(int ClosedNights, int Blocks, int Added, int Changed, int Removed)
    {
        public bool IsUnchanged => Added == 0 && Changed == 0 && Removed == 0;
    }

    private sealed record PropertyOutcome(PropertyState State, BlockChange? Change, int FeedsDeprecated)
    {
        public static readonly PropertyOutcome Skipped = new(PropertyState.Skipped, null, 0);

        public static readonly PropertyOutcome Unreadable = new(PropertyState.Unreadable, null, 0);

        public static readonly PropertyOutcome NotOpened = new(PropertyState.NotOpened, null, 0);
    }

    /// <summary>The counts of a whole run, for its log line.</summary>
    private sealed class RunTotals
    {
        public int Properties { get; private set; }

        public int ClosedNights { get; private set; }

        public int Blocks { get; private set; }

        public int Added { get; private set; }

        public int Changed { get; private set; }

        public int Removed { get; private set; }

        public int FeedsDeprecated { get; private set; }

        public int Unreadable { get; private set; }

        public int Failed { get; set; }

        public int Released { get; set; }

        public int FeedsReinstated { get; set; }

        public void Add(PropertyOutcome outcome)
        {
            switch (outcome.State)
            {
                case PropertyState.Unreadable:
                case PropertyState.NotOpened:
                    Unreadable++;
                    return;
                case PropertyState.Skipped:
                    return;
            }

            Properties++;
            FeedsDeprecated += outcome.FeedsDeprecated;
            if (outcome.Change is not { } change)
                return;

            ClosedNights += change.ClosedNights;
            Blocks += change.Blocks;
            Added += change.Added;
            Changed += change.Changed;
            Removed += change.Removed;
        }
    }
}
