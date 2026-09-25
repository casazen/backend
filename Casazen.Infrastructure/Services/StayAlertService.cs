using Casazen.Core.Entities;
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
/// own alert once instead of the "guest data missing" one. Cancelled and pending bookings get nothing.</item>
/// <item>Check-out: every booking confirmed or checked in, whether or not its arrival was registered, gets one reminder
/// at <see cref="ComplianceOptions.CheckoutReminderHourLocal"/> of its check-out day, property time
/// (<see cref="StayAlertSchedule.DueCheckoutReminder"/>). The reminder follows the booking as it is now: moved dates
/// move it, a cancellation or a check-out drops it; nothing is scheduled per booking.</item>
/// <item>Each stage is claimed before it is sent with a compare-and-set on <see cref="StayAlertState"/> (unique per
/// booking and type): a stage is delivered at most once, even with retries, a manual trigger or two runs at once. A
/// delivery that fails after the claim is logged and not repeated: an alert is never sent twice.</item>
/// <item>One run at a time: a session advisory lock (<see cref="PostgresAdvisoryLocks.Scope.StayAlertsRun"/>) on top of
/// Hangfire's <c>DisableConcurrentExecution</c>; a run that finds it taken does nothing.</item>
/// </list>
/// </remarks>
public sealed class StayAlertService(
    AppDbContext db,
    IAlloggiatiWebService alloggiatiWebService,
    INotificationService notificationService,
    IOptions<StayAlertOptions> options,
    IOptions<ComplianceOptions> complianceOptions,
    ILogger<StayAlertService> logger,
    TimeProvider? timeProvider = null) : IStayAlertService
{
    private const string RunLockKey = "stay-alerts";

    private static readonly BookingStatus[] AlloggiatiStatuses =
    [
        BookingStatus.Confirmed,
        BookingStatus.CheckedIn,
        BookingStatus.CheckedOut,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

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
            .Where(b => AlloggiatiStatuses.Contains(b.Status)
                && b.CheckInDate >= firstCheckIn
                && b.CheckInDate <= lastCheckIn)
            .OrderBy(b => b.CheckInDate)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var booking in bookings)
        {
            try
            {
                var status = await alloggiatiWebService.GetStatusAsync(booking.Id);
                if (AlloggiatiStatusRules.IsSent(status.Status))
                    continue;

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

    private async Task<int> DeliverAsync(Booking booking, StayAlertStep step, DateTime now, CancellationToken cancellationToken)
    {
        if (!await ClaimAsync(booking, step, now, cancellationToken))
            return 0;

        try
        {
            await notificationService.SendStayAlertAsync(step.Alert, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // At most once: the stage stays claimed, the next run does not send it again.
            logger.LogError(
                ex,
                "Stay alert {Kind} (stage {Stage}) of booking {BookingId} claimed but not delivered",
                step.Alert.Kind,
                step.Stage,
                booking.Id);
            return 0;
        }

        logger.LogInformation(
            "Stay alert {Kind} (stage {Stage}) sent for booking {BookingId}",
            step.Alert.Kind,
            step.Stage,
            booking.Id);
        return 1;
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

        var claimed = await db.StayAlertStates
            .Where(s => s.BookingId == booking.Id
                && s.Type == step.Type
                && (s.ReferenceDate != step.ReferenceDate || s.Stage < step.Stage))
            .ExecuteUpdateAsync(
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
