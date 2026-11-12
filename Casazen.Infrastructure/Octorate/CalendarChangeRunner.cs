using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="ICalendarChangeRunner"/> (E08/F05/US02/T02, NF-36, D7): runs the calendar changes that are due, follows what the channel manager accepted until
/// it confirms it, and sends again what failed when the host asks. The state of a chunk is never claimed beyond what the channel manager said.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Due.</b> The processes <c>Queued</c>, <c>Sending</c>, <c>Sent</c> or <c>PartiallySent</c> (its accepted chunks are still followed) whose <c>NextAttemptAt</c> is reached, oldest first, <see cref="BatchSize"/> in a run
/// and <see cref="PerOrgBatch"/> for each org, one org at a time: the quota of an org is the org's, so one that is nearly used makes its own processes wait and holds
/// no other org back. One process that fails does not stop the others; the failures are raised together at the end, so the job shows red.</item>
/// <item><b>Confirmed only when the process says so.</b> <c>GET /rest/v1/calendar/process/{id}</c> (documented only in Postman, D7): <c>NEW</c> and <c>NOT_PROCESSED</c> leave the
/// chunk accepted, <c>ERROR</c> fails it with <c>octorate_channel_error</c>, and it is confirmed only when the process and every date show a status of
/// <c>Octorate:Calendar:ProcessSuccessStatuses</c> (none is documented, so it is empty until the sandbox shows them: nothing is confirmed; an unknown, absent or interim status
/// confirms nothing); a process that is wholly confirmed is asked once more ten minutes later, so an error that shows up after the confirmation stays visible. A 404 or any answer that is a refusal means the endpoint is not
/// there for this process: the chunk stays accepted, nothing is claimed and it is not asked again. A chunk whose ids were not all kept is never confirmed (only failed
/// by an error it shows). The chunks are followed for <see cref="CalendarPushRules.ConfirmationWindow"/> after the first call.</item>
/// <item><b>Logs</b> carry ids, counts and codes.</item>
/// </list>
/// </remarks>
public sealed class CalendarChangeRunner(
    AppDbContext db,
    ICalendarPushService push,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IFeatureFlags featureFlags,
    IOptions<OctorateCalendarOptions> options,
    TimeProvider timeProvider,
    ILogger<CalendarChangeRunner> logger,
    IBackgroundJobClient? jobs = null) : ICalendarChangeRunner
{
    /// <summary>The most processes one run takes.</summary>
    public const int BatchSize = 30;

    /// <summary>The most processes of one org one run takes: the rest wait for the next run.</summary>
    public const int PerOrgBatch = 10;

    /// <summary>
    /// The most reads of the state of a process (<c>GET /rest/v1/calendar/process/{id}</c>, one unit of quota each) one run makes for an org, all its processes together: the
    /// follow-up never takes the quota the push and the other calls of the org need. What is not read waits for the next pass.
    /// </summary>
    public const int MaxReadsPerPass = 25;

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// What one run may still read for one org: at most <see cref="MaxReadsPerPass"/>, and never past the quota of the channel manager less the reserve of the push
    /// (<c>Octorate:Calendar:QuotaReserve</c>), read once for the pass.
    /// </summary>
    private sealed class FollowBudget
    {
        public int Remaining { get; set; } = MaxReadsPerPass;

        public bool QuotaRead { get; set; }
    }

    public async Task<int> RunDueAsync(CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return 0;

        var now = UtcNow;
        var due = await db.CalendarChangeProcesses
            .AsNoTracking()
            .Where(p => (p.State == CalendarChangeState.Queued
                         || p.State == CalendarChangeState.Sending
                         || p.State == CalendarChangeState.Sent
                         || p.State == CalendarChangeState.PartiallySent
                         || p.State == CalendarChangeState.Confirmed)
                        && p.NextAttemptAt != null
                        && p.NextAttemptAt <= now)
            .OrderBy(p => p.NextAttemptAt)
            .ThenBy(p => p.CreatedAt)
            .Select(p => new { p.Id, p.OrgId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var ran = 0;
        var failures = new List<Exception>();
        foreach (var org in due.GroupBy(p => p.OrgId))
        {
            // The reads of the follow-up of one org in this pass share one budget: the quota is the org's.
            var budget = new FollowBudget();
            foreach (var item in org.Take(PerOrgBatch))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await push.RunAsync(item.Id, cancellationToken);
                    await FollowAsync(item.Id, budget, cancellationToken);
                    ran++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Calendar change {ProcessId} of org {OrgId} could not be run", item.Id, item.OrgId);
                    failures.Add(ex);
                }
            }
        }

        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count} calendar change(s) could not be run", failures);

        return ran;
    }

    public async Task<CalendarChangeState> RetryAsync(Guid processId, CancellationToken cancellationToken = default)
    {
        // In a request the tenant filter hides the process of another org.
        var process = await db.CalendarChangeProcesses.FirstOrDefaultAsync(p => p.Id == processId, cancellationToken)
            ?? throw new NotFoundException("Calendar change not found")
            {
                Code = CalendarPushErrorCodes.NotFound,
                MessageKey = CalendarPushErrorCodes.NotFoundMessageKey,
            };
        if (process.State is not (CalendarChangeState.Failed or CalendarChangeState.PartiallySent))
            throw new DomainConflictException(CalendarPushErrorCodes.NotRetryable, CalendarPushErrorCodes.NotRetryableMessageKey);

        var now = UtcNow;
        var chunks = await db.CalendarChangeChunks
            .Where(c => c.ProcessId == processId && c.OrgId == process.OrgId)
            .ToListAsync(cancellationToken);
        foreach (var chunk in chunks.Where(c => c.State == CalendarChunkState.Failed))
        {
            chunk.State = CalendarChunkState.Pending;
            chunk.Attempts = 0;
            chunk.ErrorCode = null;
            chunk.UpdatedAt = now;
        }

        process.State = CalendarPushRules.Derive(chunks.Select(c => c.State));
        process.ErrorCode = null;
        process.FinishedAt = null;
        process.NextAttemptAt = now;
        await db.SaveChangesAsync(cancellationToken);

        // Once, right after the save: the recurring job is the safety net.
        jobs?.Enqueue<CalendarChangeProcessJob>(job => job.RunAsync(processId, CancellationToken.None));
        logger.LogInformation("Calendar change {ProcessId} asked again: state {State}", processId, process.State);
        return process.State;
    }

    /// <summary>
    /// Reads the quota of the channel manager once for the org in this pass and lets the follow-up read only what is left of it less the reserve of the push. A quota that cannot
    /// be read now leaves nothing to read: the chunks stay accepted and are asked again later.
    /// </summary>
    private async Task LimitToQuotaAsync(FollowBudget budget, CancellationToken ct)
    {
        if (budget.QuotaRead)
            return;

        budget.QuotaRead = true;
        try
        {
            var quota = await client.GetConfigurationAsync(ct);
            if (quota is { QuotaLimit: { } limit, QuotaStatus: { } used })
                budget.Remaining = Math.Min(budget.Remaining, Math.Max(0, limit - options.Value.Reserve - used));
        }
        catch (OctorateException ex)
        {
            budget.Remaining = 0;
            logger.LogInformation("The quota of the channel manager cannot be read now ({Code}): the follow-up of the calendar changes waits", ex.Code);
        }
    }

    /// <summary>Asks the channel manager how the chunks it accepted are doing and records what it says (and only that).</summary>
    private async Task FollowAsync(Guid processId, FollowBudget budget, CancellationToken ct)
    {
        var process = await db.CalendarChangeProcesses.FirstOrDefaultAsync(p => p.Id == processId, ct);
        if (process is null)
            return;

        var orgId = process.OrgId;
        var chunks = await db.CalendarChangeChunks.Where(c => c.ProcessId == processId && c.OrgId == orgId).ToListAsync(ct);
        // A process that is wholly confirmed is asked once more, after ConfirmedRecheckAfter: an error that shows up after the confirmation (the endpoint says so) is not left out of sight.
        var recheck = process.State == CalendarChangeState.Confirmed;
        var accepted = chunks.Where(c => c.State == CalendarChunkState.Accepted || (recheck && c.State == CalendarChunkState.Confirmed)).ToList();
        if (accepted.Count == 0)
            return;

        var successStatuses = options.Value.SuccessStatuses;
        var stop = false;
        var transient = false;
        var quotaWait = false;
        var changed = false;
        var confirmedNow = false;
        using (orgScope.Use(orgId))
        {
            await LimitToQuotaAsync(budget, ct);
            foreach (var chunk in accepted)
            {
                if (stop)
                    break;

                var (ids, complete) = CalendarPushRules.ParseProcessIds(chunk.OctorateProcessId);
                var verdicts = new List<CalendarProcessVerdict>();
                foreach (var id in ids)
                {
                    // The reads of this pass are used up, or the quota is at its reserve: what is left waits, accepted, as the push waits for a quota.
                    if (budget.Remaining <= 0)
                    {
                        transient = true;
                        stop = true;
                        quotaWait = true;
                        break;
                    }

                    budget.Remaining--;
                    try
                    {
                        verdicts.Add(CalendarPushRules.VerdictOf(await client.GetCalendarProcessAsync(id, ct), successStatuses));
                    }
                    catch (OctorateException ex) when (ex is OctorateRateLimitException or OctorateServerException)
                    {
                        // Cannot tell now (no answer, an answer that reports an error of its own, a quota that is used up: a 403 of its own type, OCT-13): asked again
                        // later, after the five minutes the push waits for a quota. Never read as «the endpoint is not there».
                        transient = true;
                        stop = true;
                        quotaWait |= ex is OctorateRateLimitException { QuotaExhausted: true };
                        logger.LogInformation("Calendar change {ProcessId}: the process of a chunk cannot be read now ({Code})", processId, ex.Code);
                        break;
                    }
                    catch (OctorateException ex)
                    {
                        // 404 or another refusal: the endpoint is not there for this process (D7). Nothing is claimed and it is not asked again.
                        stop = true;
                        logger.LogInformation("Calendar change {ProcessId}: the channel manager gives no state of the process ({Code})", processId, ex.Code);
                        break;
                    }
                }

                if (stop && verdicts.Count < ids.Count && !verdicts.Contains(CalendarProcessVerdict.Error))
                    continue;

                if (verdicts.Contains(CalendarProcessVerdict.Error))
                {
                    chunk.State = CalendarChunkState.Failed;
                    chunk.ErrorCode = CalendarPushErrorCodes.ChannelError;
                    chunk.UpdatedAt = UtcNow;
                    changed = true;
                }
                else if (chunk.State == CalendarChunkState.Accepted
                         && complete
                         && ids.Count > 0
                         && verdicts.Count == ids.Count
                         && verdicts.All(v => v == CalendarProcessVerdict.Confirmed))
                {
                    chunk.State = CalendarChunkState.Confirmed;
                    chunk.UpdatedAt = UtcNow;
                    changed = true;
                    confirmedNow = true;
                }
            }
        }

        var states = chunks.Select(c => c.State).ToList();
        var now = UtcNow;
        process.State = CalendarPushRules.Derive(states);
        process.FinishedAt = CalendarPushRules.IsSettled(states) ? process.FinishedAt ?? now : null;
        if (process.State is CalendarChangeState.Failed or CalendarChangeState.PartiallySent)
        {
            process.ErrorCode = chunks.Where(c => c.State == CalendarChunkState.Failed).OrderBy(c => c.FromDate).Select(c => c.ErrorCode).FirstOrDefault();
        }
        else if (!states.Contains(CalendarChunkState.Pending))
        {
            process.ErrorCode = null;
        }

        // While something is still to send the push sets the next attempt; what is only accepted is asked again for a while, unless the
        // endpoint is not there, and then the chunks stay accepted without being asked.
        if (!states.Contains(CalendarChunkState.Pending))
        {
            var followed = now - (process.StartedAt ?? process.CreatedAt) < CalendarPushRules.ConfirmationWindow;
            process.NextAttemptAt = states.Contains(CalendarChunkState.Accepted)
                ? (transient || !stop) && followed
                    ? now + (quotaWait ? CalendarPushRules.QuotaRetryAfter : CalendarPushRules.ConfirmationEvery)
                    : null
                : confirmedNow && process.State == CalendarChangeState.Confirmed
                    ? now + CalendarPushRules.ConfirmedRecheckAfter
                    : null;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Calendar change {ProcessId} followed: state {State}, {Changed}", processId, process.State, changed ? "chunks settled" : "no chunk settled");
    }
}
