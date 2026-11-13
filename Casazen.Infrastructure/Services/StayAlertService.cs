using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Host alerts about stays (CO-10, A5-11, A6-07, A5-25), run every hour by the <c>stay-alerts</c> job in place of the
/// hourly Alloggiati alert and the daily "check-in incomplete" reminder, which sent the same text every hour to every
/// stay with a pending communication.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Alloggiati Web: bookings confirmed, checked in or checked out whose communication is neither sent nor declared
/// sent get the stages of <see cref="StayAlertSchedule.DueAlloggiatiStep"/>; a failed or rejected communication gets its
/// own alert once instead of the "guest data missing" one. Cancelled and pending bookings get nothing, and neither does a
/// no-show (NF-45): its guests never came, so there is nothing to communicate.</item>
/// <item>Check-out: every booking confirmed or checked in, whether or not its arrival was registered, gets one reminder
/// at <see cref="ComplianceOptions.CheckoutReminderHourLocal"/> of its check-out day, property time
/// (<see cref="StayAlertSchedule.DueCheckoutReminder"/>). The reminder follows the booking as it is now: moved dates
/// move it, a cancellation, a no-show or a check-out drops it; nothing is scheduled per booking.</item>
/// <item>Each stage is claimed before it is sent with a compare-and-set on <see cref="StayAlertState"/> (unique per
/// booking and type): a stage is delivered at most once, even with retries, a manual trigger or two runs at once. A
/// delivery that fails after the claim is logged and not repeated: an alert is never sent twice.</item>
/// <item>One run at a time: a session advisory lock (<see cref="PostgresAdvisoryLocks.Scope.StayAlertsRun"/>) on top of
/// Hangfire's <c>DisableConcurrentExecution</c>; a run that finds it taken does nothing.</item>
/// <item><b>The stages of CHK-12 (E03/F02/US02/T01)</b>, only with <c>Features:ComplianceTrafficLight</c> on (the module's flag): the receipt that is
/// missing the day after the communication (<see cref="StayAlertSchedule.DueReceiptMissingStep"/>; whether it is missing is decided by the compliance
/// status engine, never by a copy of its rule), a stay of one night not entrusted to the channel manager
/// (<see cref="StayAlertSchedule.DueShortStayRiskStep"/>) and a stay over 30 nights it does not send by itself
/// (<see cref="StayAlertSchedule.DueOverThirtyNightsStep"/>); the last two only for a property whose sender is the channel manager (the engine's rule:
/// <c>Features:OctorateConnector</c> on). Every stage, the old ones included, then also opens the "To do" of its condition and, for a legal deadline
/// within 24 hours, a popup alert (<see cref="IComplianceAlertFollowUp"/>); the "To do" of a condition that cleared (the communication was sent, the data
/// are complete, the receipt arrived) or of a stay that owes nothing any more is closed here.</item>
/// </list>
/// </remarks>
public sealed class StayAlertService(
    AppDbContext db,
    IAlloggiatiWebService alloggiatiWebService,
    INotificationService notificationService,
    IOptions<StayAlertOptions> options,
    IOptions<ComplianceOptions> complianceOptions,
    ILogger<StayAlertService> logger,
    TimeProvider? timeProvider = null,
    IFeatureFlags? featureFlags = null,
    IComplianceStatusService? complianceStatus = null,
    IPropertySenderStore? senders = null,
    IComplianceAlertFollowUp? followUp = null) : IStayAlertService
{
    private const string RunLockKey = "stay-alerts";

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>The stages of CHK-12 and what they leave behind run only while the module's flag is on; without the flag (or the services) nothing changed.</summary>
    private bool ComplianceStagesOn => featureFlags?.IsEnabled(FeatureFlags.ComplianceTrafficLight) == true;

    public async Task<StayAlertRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var runLock = await PostgresAdvisoryLocks.TryAcquireSessionLockAsync(
            db, PostgresAdvisoryLocks.Scope.StayAlertsRun, RunLockKey, cancellationToken);
        if (runLock is null)
        {
            logger.LogInformation("Stay alerts run skipped: another run is in progress");
            return new StayAlertRunResult(Skipped: true, Sent: 0);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var today = _clock.TodayInRome();
        var sent = await SendAlloggiatiAlertsAsync(now, today, cancellationToken)
            + await SendCheckoutRemindersAsync(now, today, cancellationToken);

        logger.LogInformation("Stay alerts run completed: {Sent} alerts sent", sent);
        return new StayAlertRunResult(Skipped: false, Sent: sent);
    }

    private async Task<int> SendAlloggiatiAlertsAsync(DateTime now, DateTime today, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        // From the day before arrival to one day after the last stage can be due (deadline at most the day after the
        // check-in day, then the daily reminders).
        var firstCheckIn = today.AddDays(-(Math.Max(0, settings.MaxOverdueReminders) + 3));
        var lastCheckIn = today.AddDays(1);

        var bookings = await db.Bookings
            .AsNoTracking()
            .Where(b => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(b.Status)
                && b.NoShowAt == null
                && b.CheckInDate >= firstCheckIn
                && b.CheckInDate <= lastCheckIn)
            .OrderBy(b => b.CheckInDate)
            .ToListAsync(cancellationToken);

        var stagesOn = ComplianceStagesOn;
        var unsent = new List<Booking>();
        var sentIds = new List<Guid>();
        var completeIds = new List<Guid>();
        var sent = 0;
        foreach (var booking in bookings)
        {
            try
            {
                var status = await alloggiatiWebService.GetStatusAsync(booking.Id);
                if (status.DataComplete)
                    completeIds.Add(booking.Id);

                if (AlloggiatiStatusRules.IsSent(status.Status))
                {
                    sentIds.Add(booking.Id);
                    continue;
                }

                unsent.Add(booking);
                var failed = AlloggiatiStatusRules.IsFailure(status.Status);
                if (failed)
                {
                    var failure = new StayAlertStep(
                        StayAlertType.AlloggiatiFailed,
                        booking.CheckInDate.Date,
                        1,
                        new StayAlert(booking.Id, StayAlertKind.AlloggiatiFailed));
                    sent += await DeliverAsync(booking, failure, now, cancellationToken);
                }

                var step = StayAlertSchedule.DueAlloggiatiStep(booking, status.DataComplete, settings, now);
                // A failed communication already has its own alert: never "guest data missing" on top of it.
                if (step is null || (failed && step.Alert.Kind == StayAlertKind.GuestDataMissing))
                    continue;

                sent += await DeliverAsync(booking, step, now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Alloggiati alerts of booking {BookingId} could not be processed", booking.Id);
            }
        }

        if (!stagesOn)
            return sent;

        sent += await SendReceiptMissingAlertsAsync(bookings, now, today, cancellationToken);
        sent += await SendChannelManagerAlertsAsync(unsent, now, cancellationToken);
        await CloseClearedTodosAsync(sentIds, completeIds, cancellationToken);
        return sent;
    }

    private async Task<int> SendCheckoutRemindersAsync(DateTime now, DateTime today, CancellationToken cancellationToken)
    {
        var hour = complianceOptions.Value.CheckoutReminderHourLocal;
        // Property time zones around Europe: the check-out day is yesterday, today or tomorrow in Rome.
        var firstCheckOut = today.AddDays(-1);
        var lastCheckOut = today.AddDays(1);

        var bookings = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Where(b => (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
                && b.NoShowAt == null
                && b.CheckOutDate >= firstCheckOut
                && b.CheckOutDate <= lastCheckOut)
            .OrderBy(b => b.CheckOutDate)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var booking in bookings)
        {
            try
            {
                var step = StayAlertSchedule.DueCheckoutReminder(booking, booking.Property.Timezone, hour, now);
                if (step is not null)
                    sent += await DeliverAsync(booking, step, now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Check-out reminder of booking {BookingId} could not be processed", booking.Id);
            }
        }

        return sent;
    }

    // ─── The stages of CHK-12 (E03/F02/US02/T01) ───

    /// <summary>
    /// The receipt that is missing the day after the communication: asked of the compliance status engine for the stays of the window (the stays that
    /// arrived, a read of the database only: no Octorate), which says <see cref="ComplianceReasonCodes.ReceiptMissing"/> exactly when the communication
    /// was sent, entrusted or declared and no receipt is archived. This only decides when to tell. A stay whose proof is archived (green) closes the "To do"
    /// of its receipt and of its communication.
    /// </summary>
    private async Task<int> SendReceiptMissingAlertsAsync(IReadOnlyList<Booking> bookings, DateTime now, DateTime today, CancellationToken ct)
    {
        if (complianceStatus is null || bookings.Count == 0)
            return 0;

        var settings = options.Value;
        var sent = 0;
        foreach (var org in bookings.GroupBy(b => b.OrgId))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var window = new ComplianceStatusWindow(DateOnly.FromDateTime(org.Min(b => b.CheckInDate)), DateOnly.FromDateTime(today));
                var evaluation = await complianceStatus.EvaluateAsync(new HostScope(org.Key), window, ct);
                var stays = org.ToDictionary(b => b.Id);

                foreach (var item in evaluation.Items.Where(i => i.Kind == ComplianceItemKind.Alloggiati
                                                                  && i.BookingId is not null
                                                                  && i.Verdict.Light == ComplianceLight.Red
                                                                  && i.Verdict.Reason == ComplianceReasonCodes.ReceiptMissing))
                {
                    if (!stays.TryGetValue(item.BookingId!.Value, out var booking))
                        continue;

                    // The instant of the communication: the day it was sent or declared; for a stay entrusted to the channel manager, which has no report,
                    // the legal deadline (the receipt is expected by then, and missing the day after it).
                    var info = await alloggiatiWebService.GetStatusAsync(booking.Id);
                    var step = StayAlertSchedule.DueReceiptMissingStep(booking, info.ReportedAt ?? item.DueAt ?? now, settings, now);
                    if (step is not null)
                        sent += await DeliverAsync(booking, step, now, ct);
                }

                var proven = evaluation.Items
                    .Where(i => i.Kind == ComplianceItemKind.Alloggiati && i.BookingId is not null && i.Verdict.Light == ComplianceLight.Green)
                    .Select(i => i.BookingId!.Value)
                    .ToList();
                if (followUp is not null && proven.Count > 0)
                {
                    await followUp.CloseTodosAsync(
                        [ActivityType.ReceiptToRetrieve, ActivityType.AlloggiatiDeadline],
                        InternalActivity.BookingEntityType,
                        proven,
                        ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Receipt alerts of org {OrgId} could not be processed", org.Key);
            }
        }

        return sent;
    }

    /// <summary>
    /// A stay of at most one night that is not entrusted to the channel manager, and a stay over 30 nights it does not send by itself, in front of the
    /// arrival. Only for a property whose sender is the channel manager while the connector is on (<c>Features:OctorateConnector</c>): where the host
    /// sends, the Alloggiati stages already say so. The stays that were sent or declared are not here.
    /// </summary>
    private async Task<int> SendChannelManagerAlertsAsync(IReadOnlyList<Booking> unsent, DateTime now, CancellationToken ct)
    {
        if (senders is null || featureFlags is null || !featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
            return 0;

        var candidates = unsent
            .Where(b => AlloggiatiTerms.IsShortStay(b.CheckInDate, b.CheckOutDate)
                        || AlloggiatiTerms.StayDays(b.CheckInDate, b.CheckOutDate) > AlloggiatiTerms.MaxStayDaysPerSchedina)
            .ToList();
        if (candidates.Count == 0)
            return 0;

        var senderOf = await senders.GetManyAsync(candidates.Select(b => b.PropertyId).Distinct().ToList(), ct);
        var viaChannelManager = candidates
            .Where(b => senderOf.TryGetValue(b.PropertyId, out var info) && info.Applies && info.AlloggiatiSender == ComplianceSenderType.ChannelManager)
            .ToList();
        if (viaChannelManager.Count == 0)
            return 0;

        var settings = options.Value;
        var entrusted = await EntrustedStaysAsync(
            viaChannelManager.Where(b => AlloggiatiTerms.IsShortStay(b.CheckInDate, b.CheckOutDate)).Select(b => b.Id).ToList(),
            ct);

        var sent = 0;
        foreach (var booking in viaChannelManager)
        {
            try
            {
                var step = AlloggiatiTerms.IsShortStay(booking.CheckInDate, booking.CheckOutDate)
                    ? StayAlertSchedule.DueShortStayRiskStep(booking, entrusted.Contains(booking.Id), settings, now)
                    : StayAlertSchedule.DueOverThirtyNightsStep(booking, settings, now);
                if (step is not null)
                    sent += await DeliverAsync(booking, step, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Channel manager alerts of booking {BookingId} could not be processed", booking.Id);
            }
        }

        return sent;
    }

    /// <summary>
    /// The stays whose registered guests are all on the channel manager for good (sent, not as a draft): the rule of the compliance status engine
    /// (<c>ComplianceStatusService</c>). A stay with no registered guest has nothing to entrust. Only ids and positions are read, never a guest's data.
    /// </summary>
    private async Task<HashSet<Guid>> EntrustedStaysAsync(IReadOnlyCollection<Guid> bookingIds, CancellationToken ct)
    {
        if (bookingIds.Count == 0)
            return [];

        var ids = bookingIds.ToList();
        var guests = await db.StayGuests
            .AsNoTracking()
            .Where(g => ids.Contains(g.BookingId))
            .Select(g => new { g.BookingId, g.Position })
            .ToListAsync(ct);
        var links = (await db.StayGuestOctorateLinks
                .AsNoTracking()
                .Where(l => ids.Contains(l.BookingId) && l.Status == OctorateSyncStatus.Sent && !l.SentAsDraft)
                .Select(l => new { l.BookingId, l.Position })
                .ToListAsync(ct))
            .ToLookup(l => l.BookingId, l => l.Position);

        return guests
            .GroupBy(g => g.BookingId)
            .Where(stay => stay.All(guest => links[stay.Key].Contains(guest.Position)))
            .Select(stay => stay.Key)
            .ToHashSet();
    }

    /// <summary>
    /// The "To do" of a condition this service sees cleared: the communication was sent or declared (the "To do" to send it goes), the data of the guests
    /// are complete (the "To do" to complete them goes), and the stays that owe nothing any more. A proof that arrives closes its own through the event bus.
    /// </summary>
    private async Task CloseClearedTodosAsync(IReadOnlyCollection<Guid> sentIds, IReadOnlyCollection<Guid> completeIds, CancellationToken ct)
    {
        if (followUp is null)
            return;

        try
        {
            await followUp.CloseTodosAsync([ActivityType.AlloggiatiDeadline], InternalActivity.BookingEntityType, sentIds, ct);
            await followUp.CloseTodosAsync([ActivityType.CheckInPending], InternalActivity.BookingEntityType, completeIds, ct);
            await followUp.CloseTodosOfStaysThatOweNothingAsync(ComplianceFollowUps.StayTodoTypes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The To do of the stays whose condition cleared could not be closed");
        }
    }

    // ─── Delivery ───

    private async Task<int> DeliverAsync(Booking booking, StayAlertStep step, DateTime now, CancellationToken cancellationToken)
    {
        if (!await ClaimAsync(booking, step, now, cancellationToken))
            return 0;

        var delivered = true;
        try
        {
            await notificationService.SendStayAlertAsync(step.Alert, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // At most once: the stage stays claimed, the next run does not send it again.
            delivered = false;
            logger.LogError(
                ex,
                "Stay alert {Kind} (stage {Stage}) of booking {BookingId} claimed but not delivered",
                step.Alert.Kind,
                step.Stage,
                booking.Id);
        }

        await FollowUpAsync(booking, step, now, cancellationToken);

        if (!delivered)
            return 0;

        logger.LogInformation(
            "Stay alert {Kind} (stage {Stage}) sent for booking {BookingId}",
            step.Alert.Kind,
            step.Stage,
            booking.Id);
        return 1;
    }

    /// <summary>
    /// What the stage leaves behind (E03/F02/US02/T01): the "To do" of its condition and, for a legal deadline within 24 hours, a popup alert for the
    /// people who reach the property. Only with the module's flag on; a failure is logged and does not undo the stage, which is claimed.
    /// </summary>
    private async Task FollowUpAsync(Booking booking, StayAlertStep step, DateTime now, CancellationToken ct)
    {
        if (followUp is null || !ComplianceStagesOn)
            return;

        try
        {
            if (ComplianceFollowUps.TodoForStay(booking, step) is { } todo)
                await followUp.OpenTodoAsync(todo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "To do of stay alert {Kind} (stage {Stage}) of booking {BookingId} could not be raised", step.Alert.Kind, step.Stage, booking.Id);
        }

        try
        {
            if (ComplianceFollowUps.PopupForStay(booking, step) is { } popup)
                await followUp.PopupAsync(popup, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Popup alert of stay alert {Kind} (stage {Stage}) of booking {BookingId} could not be raised", step.Alert.Kind, step.Stage, booking.Id);
        }
    }

    /// <summary>
    /// Moves the booking's state for <see cref="StayAlertStep.Type"/> to <see cref="StayAlertStep.Stage"/> when it is
    /// behind (or refers to another date) in one conditional UPDATE: of two concurrent callers only one gets the row.
    /// Internal for the concurrency test.
    /// </summary>
    internal async Task<bool> ClaimAsync(Booking booking, StayAlertStep step, DateTime now, CancellationToken cancellationToken)
    {
        var exists = await db.StayAlertStates
            .AnyAsync(s => s.BookingId == booking.Id && s.Type == step.Type, cancellationToken);
        if (!exists)
        {
            var state = new StayAlertState
            {
                OrgId = booking.OrgId,
                BookingId = booking.Id,
                Type = step.Type,
                ReferenceDate = step.ReferenceDate,
                Stage = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.StayAlertStates.Add(state);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Created by a concurrent run: the conditional update below decides who sends.
            }
            finally
            {
                db.Entry(state).State = EntityState.Detached;
            }
        }

        var behind = db.StayAlertStates
            .Where(s => s.BookingId == booking.Id
                && s.Type == step.Type
                && (s.ReferenceDate != step.ReferenceDate || s.Stage < step.Stage));
        if (!db.Database.IsRelational())
        {
            // Test providers (EF InMemory) have no conditional UPDATE: the same compare-and-set, one request at a time.
            var row = await behind.FirstOrDefaultAsync(cancellationToken);
            if (row is null)
                return false;

            row.ReferenceDate = step.ReferenceDate;
            row.Stage = step.Stage;
            row.AlertCount++;
            row.LastAlertAt = now;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(row).State = EntityState.Detached;
            return true;
        }

        var claimed = await behind.ExecuteUpdateAsync(
            set => set
                .SetProperty(s => s.ReferenceDate, step.ReferenceDate)
                .SetProperty(s => s.Stage, step.Stage)
                .SetProperty(s => s.AlertCount, s => s.AlertCount + 1)
                .SetProperty(s => s.LastAlertAt, (DateTime?)now)
                .SetProperty(s => s.UpdatedAt, now),
            cancellationToken);
        return claimed == 1;
    }
}
