using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IServiceRequestReminderService"/>
/// <remarks>
/// <para><b>A system job.</b> It reads the showcase requests of every supplier that the reminder concerns (taken, with hours,
/// not yet reminded): the statement is <see cref="ReminderCandidatesOf"/>, a predicate on the request itself. It writes one
/// field, <c>ReminderSentAt</c>, and tells the customer through <see cref="ShowcaseBookingNotifier"/>.</para>
/// <para><b>At most once.</b> The request is marked, and the mark is saved only if nobody touched the request since it was read
/// (<c>xmin</c>: a supplier that cancelled it meanwhile wins), before the e-mail is queued: a run that crashes or repeats
/// between the two sends the reminder once or not at all, never twice.</para>
/// </remarks>
public sealed class ServiceRequestReminderService(
    AppDbContext db,
    ShowcaseBookingNotifier notifier,
    ILogger<ServiceRequestReminderService> logger,
    TimeProvider? timeProvider = null) : IServiceRequestReminderService
{
    internal const string RunLockKey = "service-request-reminders";

    /// <summary>Requests one run handles at most (an hour's reminders are few; the bound keeps a run short whatever happens).</summary>
    public const int MaxPerRun = 500;

    /// <summary>
    /// How far ahead a work can start for its reminder to be due: the reminder is due at 18:00 of the day before, so at the latest
    /// when the work is less than 30 hours away; two days is a safe superset the exact rule then narrows.
    /// </summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(48);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ServiceRequestReminderRun> SendDueAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.ServiceRequestRemindersRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Service request reminders run skipped: another run is in progress");
            return ServiceRequestReminderRun.LockTaken;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var candidates = await ReminderCandidatesOf(db, now, now + Horizon).ToListAsync(cancellationToken);

        int sent = 0, failed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ServiceRequestReminderRules.IsDue(candidate.StartUtc, candidate.TakenAt, now))
                continue;

            switch (await RemindAsync(candidate.Id, now, cancellationToken))
            {
                case true:
                    sent++;
                    break;
                case false:
                    failed++;
                    break;
                default:
                    break;
            }
        }

        if (sent > 0 || failed > 0)
            logger.LogInformation("Service request reminders: {Sent} queued, {Failed} could not be queued", sent, failed);
        return new ServiceRequestReminderRun(Skipped: false, sent, failed);
    }

    /// <summary>
    /// The showcase requests the supplier took (<c>PresoInCarico</c>) that have hours between <paramref name="fromUtc"/> and
    /// <paramref name="toUtc"/> and no reminder yet, the earliest work first, at most <see cref="MaxPerRun"/>. Only what the rule
    /// needs: the id, the start and the instant the request was taken. ServiceRequest has two parties and no tenant filter
    /// (TN-2 allow-list): the job works across every supplier, so the filters of the host are off.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<ReminderCandidate> ReminderCandidatesOf(AppDbContext db, DateTime fromUtc, DateTime toUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.Status == ServiceRequestStatus.PresoInCarico
                        && r.ReminderSentAt == null
                        && r.ScheduledStartUtc != null
                        && r.ScheduledStartUtc > fromUtc
                        && r.ScheduledStartUtc <= toUtc)
            .OrderBy(r => r.ScheduledStartUtc)
            .ThenBy(r => r.Id)
            .Select(r => new ReminderCandidate(r.Id, r.ScheduledStartUtc!.Value, r.TakenAt))
            .Take(MaxPerRun);

    /// <summary>A request whose reminder may be due: when the work starts and when the supplier took the request.</summary>
    internal sealed record ReminderCandidate(Guid Id, DateTime StartUtc, DateTime? TakenAt);

    /// <summary>
    /// Marks the request and queues its reminder. <c>true</c> queued; <c>false</c> could not be queued (no one to write to, an
    /// error); <c>null</c> not done because the request changed under the run (cancelled, reminded by another run).
    /// </summary>
    private async Task<bool?> RemindAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        try
        {
            // One request at a time, read again: what the run listed a moment ago may have changed since.
            var request = await db.ServiceRequests
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (request is not { Status: ServiceRequestStatus.PresoInCarico, ReminderSentAt: null, ScheduledStartUtc: not null }
                || !ServiceRequestReminderRules.IsDue(request.ScheduledStartUtc.Value, request.TakenAt, now))
            {
                return null;
            }

            request.ReminderSentAt = now;
            await db.SaveChangesAsync(cancellationToken);

            var queued = await notifier.TryQueueReminderAsync(request, cancellationToken);
            if (!queued)
                logger.LogWarning("The reminder of showcase request {Id} could not be queued", id);
            return queued;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("ServiceRequest {Id}: changed while the reminders run was marking it, left as it is", id);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ServiceRequest {Id} could not be reminded (retried by the next run)", id);
            return false;
        }
    }
}
