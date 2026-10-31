using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IShowcaseReplyReminderService"/>
/// <remarks>
/// <para><b>A system pass.</b> It works across every supplier on purpose: the requests it looks at are the ones whose deadline is near, whoever they
/// belong to. It is run only by <see cref="ServiceRequestExpiryService"/> (job <c>service-request-expiry</c>, under its session lock) and reachable
/// from no request. <see cref="WaitingOf"/> lists the candidates with the few columns the rule needs; only a request for which a reminder is due is
/// read in full.</para>
/// <para><b>Once, and never for a supplier that answered.</b> The request is marked (<c>ReplyReminderHalfSentAt</c> / <c>ReplyReminderLastSentAt</c>) and
/// <b>saved first</b>, only if nobody touched it since it was read (<c>xmin</c>); the e-mail and the push are queued after, by the winner only. A
/// crash between the two sends the reminder zero times, never twice; a supplier whose message, offer or refusal is saved at the same moment wins
/// (the save here conflicts, nothing is sent) and one that answered before has no deadline left to remind it of.</para>
/// <para>A supplier that is not <c>Active</c> is not reminded (it cannot answer) and the request is not marked: if it is reactivated within the
/// time, the reminder still comes.</para>
/// </remarks>
public sealed class ShowcaseReplyReminderService(
    AppDbContext db,
    ShowcaseBookingNotifier notifier,
    ILogger<ShowcaseReplyReminderService> logger,
    TimeProvider? timeProvider = null) : IShowcaseReplyReminderService
{
    /// <summary>Requests one pass looks at, at most: the ones waiting for an answer, nearest deadline first (a day is the longest wait there can be).</summary>
    public const int MaxPerRun = 500;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShowcaseReplyReminderRun> SendDueAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var waiting = await WaitingOf(db, now).ToListAsync(cancellationToken);
        var due = waiting
            .Where(w => ShowcaseReplyReminderRules.DueOf(w.CreatedAt, w.DueAt, now, w.HalfSent, lastSent: false) != ShowcaseReplyReminder.None)
            .Select(w => w.Id)
            .ToList();
        if (due.Count == 0)
            return ShowcaseReplyReminderRun.Empty;

        int sent = 0, conflicts = 0, failed = 0;
        foreach (var id in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await RemindAsync(id, now, cancellationToken))
            {
                case Outcome.Sent:
                    sent++;
                    break;
                case Outcome.Conflict:
                    conflicts++;
                    break;
                case Outcome.Failed:
                    failed++;
                    break;
            }
        }

        logger.LogInformation(
            "Reply reminders: {Sent} queued, {Conflicts} changed under the run, {Failed} failed",
            sent, conflicts, failed);
        return new ShowcaseReplyReminderRun(sent, conflicts, failed);
    }

    /// <summary>
    /// The requests on measure of the public showcases that wait for the supplier's first answer at <paramref name="nowUtc"/>: new, with a deadline
    /// still ahead, no offer waiting for the customer and the last reminder not yet sent, nearest deadline first, at most <see cref="MaxPerRun"/>.
    /// Only the id, the creation (when the wait began), the deadline and whether the half reminder was sent: no payload, nothing the customer wrote.
    /// ServiceRequest has two parties and no tenant filter (TN-2 allow-list): the pass works across every org, so the filters of the host are off
    /// and the predicate is the state of the request itself.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<WaitingRequest> WaitingOf(AppDbContext db, DateTime nowUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase
                        && r.Kind == ServiceRequestKind.Custom
                        && r.Status == ServiceRequestStatus.Richiesto
                        && r.ResponseDueAt != null
                        && r.ResponseDueAt > nowUtc
                        && r.QuoteSentAt == null
                        && r.ReplyReminderLastSentAt == null)
            .OrderBy(r => r.ResponseDueAt)
            .ThenBy(r => r.Id)
            .Select(r => new WaitingRequest(r.Id, r.CreatedAt, r.ResponseDueAt!.Value, r.ReplyReminderHalfSentAt != null))
            .Take(MaxPerRun);

    /// <summary>A request waiting for its supplier's first answer: what <see cref="ShowcaseReplyReminderRules.DueOf"/> needs.</summary>
    internal sealed record WaitingRequest(Guid Id, DateTime CreatedAt, DateTime DueAt, bool HalfSent);

    private enum Outcome
    {
        Sent,
        NotDue,
        Conflict,
        Failed,
    }

    private async Task<Outcome> RemindAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        // One request at a time, read again: what the pass listed a moment ago may have been answered since.
        db.ChangeTracker.Clear();
        try
        {
            var request = await db.ServiceRequests
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

            if (request is null
                || !ShowcaseReplyReminderRules.Applies(request.RentalContext, request.Kind, request.Status, request.ResponseDueAt, request.QuoteSentAt))
            {
                return Outcome.Conflict;
            }

            var reminder = ShowcaseReplyReminderRules.DueOf(
                request.CreatedAt,
                request.ResponseDueAt!.Value,
                now,
                request.ReplyReminderHalfSentAt is not null,
                request.ReplyReminderLastSentAt is not null);
            if (reminder == ShowcaseReplyReminder.None)
                return Outcome.NotDue;

            // A supplier that is not active cannot answer: nothing is sent and nothing is marked (SupplierProfile is keyed by the supplier org).
            var status = await db.SupplierProfiles
                .AsNoTracking()
                .Where(sp => sp.OrgId == request.SupplierOrgId)
                .Select(sp => (SupplierStatus?)sp.Status)
                .FirstOrDefaultAsync(cancellationToken);
            if (status != SupplierStatus.Active)
                return Outcome.NotDue;

            if (reminder == ShowcaseReplyReminder.Last)
                request.ReplyReminderLastSentAt = now;
            else
                request.ReplyReminderHalfSentAt = now;

            // Saved only if the request is as it was read (xmin): a supplier that answered meanwhile wins and is not reminded.
            await db.SaveChangesAsync(cancellationToken);

            await notifier.NotifySupplierOfReplyReminderAsync(request, lastCall: reminder == ShowcaseReplyReminder.Last, cancellationToken);
            return Outcome.Sent;
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("ServiceRequest {Id}: changed while the reminder pass was reading it, no reminder", id);
            return Outcome.Conflict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogError(ex, "ServiceRequest {Id} could not be reminded (retried by the next run)", id);
            return Outcome.Failed;
        }
    }
}
