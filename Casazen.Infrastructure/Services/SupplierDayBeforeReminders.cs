using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The reminder of the day before the work, to the <b>supplier</b> (SP-16d): the second pass of the job <c>service-request-reminders</c>
/// (<see cref="ServiceRequestReminderService"/>, which holds the run lock and calls it), for a request the supplier took in <b>any context</b> —
/// a host's short or long stay, or the public showcase —, at 18:00 Europe/Rome of the day before (<see cref="ServiceRequestReminderRules"/>, daylight
/// saving time included), once.
/// </summary>
/// <remarks>
/// <para><b>Once.</b> The request is marked (<see cref="ServiceRequest.SupplierReminderSentAt"/>) in a save that carries the row version (<c>xmin</c>: a
/// request the supplier or the customer changed meanwhile wins and is left as it is) <b>before</b> the notice is queued, so a run that crashes or repeats in
/// between sends the reminder once or not at all, never twice. The mark is also written when nothing is sent because the supplier turned the reminder
/// off or is suspended: the request was judged, and it is not read again at every run (nor does a supplier that turns the switch on at night get the
/// reminder late).</para>
/// <para><b>What it sends</b> is decided by <see cref="ServiceRequestNotifier.NotifySupplierDayBeforeAsync"/>: e-mail and push with the service, the
/// time and the comune, never the address or the contact of the customer. A request taken after the reminder time gets none
/// (<see cref="ServiceRequestReminderRules.IsDue"/>): the supplier took it a moment ago. Requests that existed before the column are judged by the
/// same rule, so a job that is still ahead is reminded once at the first run after the deploy if its time has come.</para>
/// </remarks>
public sealed class SupplierDayBeforeReminders(AppDbContext db, ServiceRequestNotifier notifier, ILogger<SupplierDayBeforeReminders> logger)
{
    /// <summary>The same bound as the customers' pass: a run is short whatever happens.</summary>
    public const int MaxPerRun = ServiceRequestReminderService.MaxPerRun;

    /// <summary>How far ahead a work can start for its reminder to be due (the reminder is at 18:00 of the day before: less than 30 hours); two days is a safe superset.</summary>
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(48);

    /// <summary>
    /// One pass at <paramref name="now"/>: the reminders that are due and not yet decided. Called with the run lock of the job held.
    /// <c>Sent</c> counts the notices queued, <c>Failed</c> the ones that could not be queued; one skipped on purpose (switch off, supplier suspended,
    /// request changed) counts as neither.
    /// </summary>
    public async Task<(int Sent, int Failed)> SendDueAsync(DateTime now, CancellationToken cancellationToken)
    {
        var candidates = await CandidatesOf(db, now, now + Horizon).ToListAsync(cancellationToken);

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
            logger.LogInformation("Service request reminders to suppliers: {Sent} queued, {Failed} could not be queued", sent, failed);
        return (sent, failed);
    }

    /// <summary>
    /// The requests the supplier took (<c>PresoInCarico</c>), in any context, that have hours between <paramref name="fromUtc"/> and
    /// <paramref name="toUtc"/> and no decision yet, the earliest work first, at most <see cref="MaxPerRun"/>. Only what the rule needs: the id, the start
    /// and the instant the request was taken. ServiceRequest has two parties and no tenant filter (TN-2 allow-list): the job works across every supplier,
    /// so the filters of the host are off.
    /// </summary>
    /// <remarks>Static and internal so a test can read the SQL it becomes on the PostgreSQL provider without a server.</remarks>
    internal static IQueryable<Candidate> CandidatesOf(AppDbContext db, DateTime fromUtc, DateTime toUtc) =>
        db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.Status == ServiceRequestStatus.PresoInCarico
                        && r.SupplierReminderSentAt == null
                        && r.ScheduledStartUtc != null
                        && r.ScheduledStartUtc > fromUtc
                        && r.ScheduledStartUtc <= toUtc)
            .OrderBy(r => r.ScheduledStartUtc)
            .ThenBy(r => r.Id)
            .Select(r => new Candidate(r.Id, r.ScheduledStartUtc!.Value, r.TakenAt))
            .Take(MaxPerRun);

    /// <summary>A request whose reminder may be due: when the work starts and when the supplier took the request.</summary>
    internal sealed record Candidate(Guid Id, DateTime StartUtc, DateTime? TakenAt);

    /// <summary>
    /// Marks the request and queues its reminder. <c>true</c> queued; <c>false</c> could not be queued (an error, no profile to write to); <c>null</c> not
    /// sent: the request changed under the run (cancelled, reminded by another run), or the supplier turned the reminder off or is suspended.
    /// </summary>
    private async Task<bool?> RemindAsync(Guid id, DateTime now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        try
        {
            // One request at a time, read again with its property (for the comune): what the run listed a moment ago may have changed since.
            var request = await db.ServiceRequests
                .IgnoreQueryFilters()
                .Include(r => r.Property)
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (request is not { Status: ServiceRequestStatus.PresoInCarico, SupplierReminderSentAt: null, ScheduledStartUtc: not null }
                || !ServiceRequestReminderRules.IsDue(request.ScheduledStartUtc.Value, request.TakenAt, now))
            {
                return null;
            }

            request.SupplierReminderSentAt = now;
            await db.SaveChangesAsync(cancellationToken);

            var outcome = await notifier.NotifySupplierDayBeforeAsync(request, cancellationToken);
            if (outcome == false)
                logger.LogWarning("The reminder of the day before to the supplier of service request {Id} could not be queued", id);
            return outcome;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("ServiceRequest {Id}: changed while the reminders run was marking it for the supplier, left as it is", id);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "ServiceRequest {Id} could not be reminded to its supplier (retried by the next run unless it was already marked)", id);
            return false;
        }
    }
}
