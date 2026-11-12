using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="ICalendarPushService"/> (E08/F05/US02/T01, NF-36, NF-78, ADR-023-R8): sends the pending chunks of a calendar change to the channel manager and
/// records what it answered. See the interface for what an answer means.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>What is sent is built at send time.</b> The entries of a chunk are made from the rules, the prices and the blocks of the property as they are when the
/// chunk is sent (<see cref="OctorateCalendarEntries"/>), so a chunk that waited sends the values of its nights as they are now, never an older version of them.</item>
/// <item><b>Org by org.</b> The job has no tenant: every query names the org of the process, and the calls to the channel manager run under
/// <see cref="IOctorateOrgScope.Use"/> of that org.</item>
/// <item><b>One run at a time for a process</b> (session lock <c>CalendarChangeProcess</c>): the run enqueued after a change and the recurring job never send the same
/// chunk twice; a run that finds the lock taken leaves, and the other one does the work.</item>
/// <item><b>The quota is read once for a run</b> and every chunk adds its estimate to what the run has used, so a run never goes past the limit minus the reserve.
/// What does not fit waits, with <c>octorate_quota_low</c> on the process and the next attempt five minutes later.</item>
/// <item><b>Nothing is swallowed.</b> The errors of the channel manager are recorded on the chunk and drive the retry; any other failure goes up after the process
/// is left with a later <c>NextAttemptAt</c>, so a bug is not retried every minute.</item>
/// <item><b>Logs</b> carry ids, counts and codes: never a body, a price or a name.</item>
/// </list>
/// </remarks>
public sealed class CalendarPushService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateBookingTargets targets,
    IPropertyDayRuleService dayRules,
    IPricingAdapterService pricing,
    IFeatureFlags featureFlags,
    IConfiguration configuration,
    IOptions<OctorateCalendarOptions> options,
    TimeProvider timeProvider,
    ILogger<CalendarPushService> logger) : ICalendarPushService
{
    /// <summary>What of a property the entries are made from.</summary>
    private sealed record PropertyRow(decimal NightlyRate, decimal WeekendSurchargePercent, int? MinNights, string Timezone);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task RunAsync(Guid processId, CancellationToken cancellationToken = default)
    {
        // Nothing leaves while the connector is off: the chunks wait, and the job that picks them up is not scheduled either.
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation("Calendar change {ProcessId} not sent: the Octorate connector is off", processId);
            return;
        }

        // The job has no tenant: the process is found by its id, and its org is the one every other query names.
        var process = await db.CalendarChangeProcesses.FirstOrDefaultAsync(p => p.Id == processId, cancellationToken);
        if (process is null || !IsToSend(process.State))
            return;

        await using var hold = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.CalendarChangeProcess, processId.ToString("N"), cancellationToken);
        if (hold is null)
        {
            logger.LogInformation("Calendar change {ProcessId} is being sent by another worker: this run leaves it", processId);
            return;
        }

        // Read again inside the lock: the run that held it may have settled the process while this one waited.
        await db.Entry(process).ReloadAsync(cancellationToken);
        if (!IsToSend(process.State))
            return;

        var orgId = process.OrgId;
        var pending = await db.CalendarChangeChunks
            .Where(c => c.ProcessId == processId && c.OrgId == orgId && c.State == CalendarChunkState.Pending)
            .OrderBy(c => c.FromDate)
            .ThenBy(c => c.PropertyId)
            .ToListAsync(cancellationToken);

        var run = new RunState();
        try
        {
            if (pending.Count > 0)
            {
                using (orgScope.Use(orgId))
                {
                    foreach (var chunk in pending)
                    {
                        if (run.Stop)
                            break;

                        await SendAsync(orgId, chunk, run, cancellationToken);
                    }
                }
            }

            await FinishAsync(process, run, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not the channel manager's error (those are recorded on the chunk): a bug or the database. It goes up, and the process is left with a later
            // attempt so the recurring job does not hit it every minute.
            await PostponeAfterFailureAsync(process, cancellationToken);
            throw;
        }
    }

    // --- One chunk ---------------------------------------------------------------------------------------------------------------

    private async Task SendAsync(Guid orgId, CalendarChangeChunk chunk, RunState run, CancellationToken ct)
    {
        var settings = options.Value;
        try
        {
            var property = await db.Properties
                .AsNoTracking()
                .Where(p => p.Id == chunk.PropertyId && p.OrgId == orgId)
                .Select(p => new PropertyRow(p.NightlyRate, p.WeekendSurchargePercent, p.MinNights, p.Timezone))
                .FirstOrDefaultAsync(ct);
            if (property is null)
            {
                Settle(chunk, CalendarChunkState.Skipped, null);
                await db.SaveChangesAsync(ct);
                return;
            }

            // Where the property books now: the mapping may have been changed or the connection lost since the change was recorded.
            var lookup = await targets.LookupAsync(chunk.PropertyId, ct);
            if (lookup.Gap == OctorateTargetGap.NotMirrored)
            {
                Settle(chunk, CalendarChunkState.Skipped, null);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (lookup.Target is not { } target)
            {
                Settle(chunk, CalendarChunkState.Failed, CalendarPushErrorCodes.RoomNotChosen);
                await db.SaveChangesAsync(ct);
                return;
            }

            // The days before today of the property are never sent: a chunk that waited past its last day has nothing to send.
            var today = DayRuleRules.TodayOf(property.Timezone, timeProvider.GetUtcNow());
            if (CalendarChangeRules.PushWindow(today, chunk.FromDate, chunk.ToDate) is not { } window)
            {
                Settle(chunk, CalendarChunkState.Skipped, null);
                await db.SaveChangesAsync(ct);
                return;
            }

            var entries = await BuildEntriesAsync(orgId, chunk.PropertyId, property, target, window.From, window.To, ct);
            if (entries.Count == 0)
            {
                Settle(chunk, CalendarChunkState.Skipped, null);
                await db.SaveChangesAsync(ct);
                return;
            }

            // The quota is read once for the run, and what the run has already used is added to it: a chunk that does not fit waits, it never fails.
            var units = CalendarChunkPlanner.EstimateQuotaUnits(window.From, window.To, settings.MaxDays, settings.RoomDaysPerUnit);
            if (!run.QuotaRead)
            {
                run.Quota = await client.GetConfigurationAsync(ct);
                run.QuotaRead = true;
            }

            if (run.Quota is { QuotaLimit: { } limit, QuotaStatus: { } used } && used + run.UnitsUsed + units > limit - settings.Reserve)
            {
                run.Postponed = true;
                run.Stop = true;
                logger.LogInformation(
                    "Calendar change {ProcessId} waits: the quota of the channel manager is nearly used (chunk of {Units} units)", chunk.ProcessId, units);
                return;
            }

            var result = await client.UpdateCalendarAsync(entries, ct);
            run.Calls++;
            run.UnitsUsed += units;
            run.StartedAt ??= UtcNow;
            chunk.Attempts++;
            chunk.LastAttemptAt = UtcNow;

            // An answer with no process id wrote nothing (a room with no license answers 200 so): it is never an acceptance.
            if (result.ProcessIds.Count == 0)
            {
                Settle(chunk, CalendarChunkState.Failed, CalendarPushErrorCodes.RoomNotLicensed);
            }
            else
            {
                chunk.OctorateProcessId = CalendarPushRules.JoinProcessIds(result.ProcessIds);
                Settle(chunk, CalendarChunkState.Accepted, null);
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OctorateRateLimitException ex)
        {
            await RecordTransientAsync(chunk, run, ex, ct);
        }
        catch (OctorateServerException ex)
        {
            await RecordTransientAsync(chunk, run, ex, ct);
        }
        catch (OctorateAuthException ex) when (ex.IsNotConnected)
        {
            // The connection of the org is gone: nothing is sent for any chunk of this run, and nothing is claimed.
            logger.LogWarning("Calendar change {ProcessId}: the org has no connection to the channel manager any more", chunk.ProcessId);
            await SettleRestAsync(orgId, chunk, run, CalendarChunkState.Skipped, null, ct);
        }
        catch (OctorateException ex) when (ex is OctorateAuthException or OctorateWrongEnvironmentException)
        {
            // The same token serves every chunk of the org: they would all be refused, so none is sent.
            logger.LogWarning("Calendar change {ProcessId}: the access of CasaZen was refused ({Code})", chunk.ProcessId, ex.Code);
            await SettleRestAsync(orgId, chunk, run, CalendarChunkState.Failed, CalendarPushErrorCodes.NotAuthorized, ct);
        }
        catch (OctorateException ex)
        {
            // Octorate refused this request (a 4xx, or an answer that says it did not take it): not tried again by itself, the host can ask again.
            logger.LogWarning(
                "Calendar change {ProcessId}: refused by the channel manager ({Code}, HTTP {Status})", chunk.ProcessId, ex.Code, ex.OctorateStatusCode);
            chunk.Attempts++;
            chunk.LastAttemptAt = UtcNow;
            Settle(chunk, CalendarChunkState.Failed, CalendarPushErrorCodes.Rejected);
            await db.SaveChangesAsync(ct);
        }
        catch (ArgumentException ex)
        {
            // A body CasaZen cannot make (a room that is not a number): nothing left CasaZen, and there is nothing to retry until the mapping is fixed.
            logger.LogWarning(ex, "Calendar change {ProcessId}: the chunk cannot be written, not sent", chunk.ProcessId);
            Settle(chunk, CalendarChunkState.Failed, CalendarPushErrorCodes.Rejected);
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// A rate limit, a 5xx or a timeout: the chunk stays <c>Pending</c> with one more attempt and the next one a back-off later (1, 2, 4, 8 minutes), and the run
    /// stops (the channel manager is not answering, the next chunks would only be refused); after the fifth attempt the chunk is <c>Failed</c> with
    /// <c>octorate_unavailable</c>.
    /// </summary>
    private async Task RecordTransientAsync(CalendarChangeChunk chunk, RunState run, OctorateException error, CancellationToken ct)
    {
        chunk.Attempts++;
        chunk.LastAttemptAt = UtcNow;
        chunk.UpdatedAt = UtcNow;
        run.Stop = true;
        run.StartedAt ??= UtcNow;
        if (chunk.Attempts >= CalendarPushRules.MaxAttempts)
        {
            Settle(chunk, CalendarChunkState.Failed, CalendarPushErrorCodes.Unavailable);
        }
        else
        {
            run.RetryAt = Earliest(run.RetryAt, UtcNow + CalendarPushRules.BackoffAfter(chunk.Attempts));
        }

        logger.LogWarning(
            "Calendar change {ProcessId}: the channel manager did not answer ({Code}, HTTP {Status}), attempt {Attempt} of {Max}",
            chunk.ProcessId,
            error.Code,
            error.OctorateStatusCode,
            chunk.Attempts,
            CalendarPushRules.MaxAttempts);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>This chunk and every pending chunk of the process settled the same way, and the run stopped.</summary>
    private async Task SettleRestAsync(
        Guid orgId,
        CalendarChangeChunk chunk,
        RunState run,
        CalendarChunkState state,
        string? code,
        CancellationToken ct)
    {
        run.Stop = true;
        Settle(chunk, state, code);
        var others = await db.CalendarChangeChunks
            .Where(c => c.ProcessId == chunk.ProcessId && c.OrgId == orgId && c.State == CalendarChunkState.Pending && c.Id != chunk.Id)
            .ToListAsync(ct);
        foreach (var other in others)
            Settle(other, state, code);

        await db.SaveChangesAsync(ct);
    }

    private void Settle(CalendarChangeChunk chunk, CalendarChunkState state, string? code)
    {
        chunk.State = state;
        chunk.ErrorCode = code;
        chunk.UpdatedAt = UtcNow;
    }

    // --- What is sent ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The entries of the nights of a chunk as they are now: the rules and the prices the host set, the nights its manual blocks close, and the nights something
    /// else holds (a booking, a hold, a block of a channel), whose sale is not written.
    /// </summary>
    private async Task<IReadOnlyList<OctorateCalendarUpdate>> BuildEntriesAsync(
        Guid orgId,
        Guid propertyId,
        PropertyRow property,
        OctorateBookingTarget target,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var toExclusive = to.AddDays(1);
        var rules = await dayRules.GetRulesAsync(new HostScope(orgId), propertyId, from, toExclusive, ct);
        var hostPrices = await pricing.GetAppliedNightlyPricesAsync(propertyId, from, toExclusive, ct);

        // The nights the manual blocks close (a mode change block is a manual block too).
        var start = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = toExclusive.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var blocks = await db.CalendarBlocks
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && b.PropertyId == propertyId && b.Source == CalendarBlockSource.Manual)
            .Where(b => b.StartUtc < end && b.EndUtc > start)
            .Select(b => new { b.StartUtc, b.EndUtc })
            .ToListAsync(ct);
        var closed = blocks
            .SelectMany(b => OctorateClosedNights.NightsIn(b.StartUtc, b.EndUtc, from, to))
            .ToHashSet();

        // What is taken by anything else is left alone: the public definition of a taken night, less the nights our own blocks close.
        var occupancy = new PublicOccupancyReader(db, configuration, timeProvider);
        var taken = await occupancy.TakenNightsAsync([propertyId], start, end, ct);
        var held = taken.TryGetValue(propertyId, out var nights)
            ? nights.Select(DateOnly.FromDateTime).Where(night => !closed.Contains(night)).ToHashSet()
            : new HashSet<DateOnly>();

        return OctorateCalendarEntries.Build(
            new OctorateCalendarProperty(property.NightlyRate, property.WeekendSurchargePercent, property.MinNights),
            target,
            from,
            to,
            rules,
            hostPrices,
            closed,
            held,
            options.Value);
    }

    // --- The process -------------------------------------------------------------------------------------------------------------

    private async Task FinishAsync(CalendarChangeProcess process, RunState run, CancellationToken ct)
    {
        var chunks = await db.CalendarChangeChunks
            .AsNoTracking()
            .Where(c => c.ProcessId == process.Id && c.OrgId == process.OrgId)
            .Select(c => new { c.State, c.ErrorCode, c.FromDate })
            .ToListAsync(ct);
        var states = chunks.Select(c => c.State).ToList();
        var now = UtcNow;

        process.State = CalendarPushRules.Derive(states);
        if (run.Calls > 0)
            process.Attempts++;

        process.StartedAt ??= run.StartedAt;
        process.FinishedAt = CalendarPushRules.IsSettled(states) ? process.FinishedAt ?? now : null;
        process.ErrorCode = run.Postponed
            ? CalendarPushErrorCodes.QuotaLow
            : run.RetryAt is not null
                ? CalendarPushErrorCodes.Unavailable
                : process.State is CalendarChangeState.Failed or CalendarChangeState.PartiallySent
                    ? chunks.Where(c => c.State == CalendarChunkState.Failed).OrderBy(c => c.FromDate).Select(c => c.ErrorCode).FirstOrDefault()
                    : null;

        // When the job asks again: a quota that is nearly used waits five minutes, an attempt that failed waits its back-off, what was accepted is
        // followed (CalendarChangeRunner) until the channel manager confirms it or the window of the confirmation is over.
        if (states.Contains(CalendarChunkState.Pending))
        {
            process.NextAttemptAt = run.Postponed
                ? now + CalendarPushRules.QuotaRetryAfter
                : run.RetryAt ?? now + CalendarPushRules.BackoffAfter(1);
        }
        else if (states.Contains(CalendarChunkState.Accepted))
        {
            process.NextAttemptAt = now + CalendarPushRules.ConfirmationEvery;
        }
        else
        {
            process.NextAttemptAt = null;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Calendar change {ProcessId} run: {Calls} calls, state {State}{Quota}",
            process.Id,
            run.Calls,
            process.State,
            run.Postponed ? " (waiting for quota)" : string.Empty);
    }

    // After a failure that is not the channel manager's: the next attempt a back-off later, so a bug is not retried every minute. A save that fails
    // as well is logged and the first error is the one that goes up.
    private async Task PostponeAfterFailureAsync(CalendarChangeProcess process, CancellationToken ct)
    {
        try
        {
            process.Attempts++;
            process.NextAttemptAt = UtcNow + CalendarPushRules.BackoffAfter(process.Attempts);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Calendar change {ProcessId}: the later attempt could not be recorded", process.Id);
        }
    }

    private static bool IsToSend(CalendarChangeState state) => state is CalendarChangeState.Queued or CalendarChangeState.Sending;

    private static DateTime Earliest(DateTime? current, DateTime candidate) => current is { } known && known < candidate ? known : candidate;

    /// <summary>What a run learns and decides as it goes.</summary>
    private sealed class RunState
    {
        public bool Stop { get; set; }

        public bool Postponed { get; set; }

        public bool QuotaRead { get; set; }

        public OctorateConfiguration? Quota { get; set; }

        public int UnitsUsed { get; set; }

        public int Calls { get; set; }

        public DateTime? StartedAt { get; set; }

        public DateTime? RetryAt { get; set; }
    }
}
