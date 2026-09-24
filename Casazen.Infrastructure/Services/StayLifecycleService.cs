using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Arrival and departure of a stay (CO-08, A5-08). See <see cref="IStayLifecycleService"/> and
/// <see cref="StayLifecycleRules"/>.
/// </summary>
/// <remarks>
/// Each transition takes the advisory lock of the booking used by the cancellation and the host changes (BK-02,
/// PC-07), reads the booking again under it, checks the rules and saves in the same transaction. Hangfire jobs are
/// scheduled around it: the check-out reminder before the commit (deleted when the commit fails; the job skips a booking
/// that is not checked in anyway), the Alloggiati job after it (idempotent per booking and guest, CO-11).
/// </remarks>
public sealed class StayLifecycleService(
    AppDbContext db,
    IAlloggiatiWebService alloggiatiWebService,
    IAlloggiatiReportScheduler alloggiatiReportScheduler,
    ICheckoutReminderScheduler checkoutReminderScheduler,
    IServiceRequestService serviceRequestService,
    IOptions<ComplianceOptions> complianceOptions,
    ILogger<StayLifecycleService> logger,
    TimeProvider? timeProvider = null) : IStayLifecycleService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<StayArrivalResult> RegisterArrivalAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        Booking booking;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);
            var today = _clock.TodayInRome();
            if (StayLifecycleRules.ArrivalError(booking, today) is { } error)
                throw error;

            MarkArrived(booking, today);
            await SaveWithReminderAsync(booking, transaction, cancellationToken);
        }

        await ScheduleAlloggiatiAsync(booking.Id);
        var guestDataComplete = await alloggiatiWebService.IsStayDataCompleteAsync(booking.Id);
        logger.LogInformation(
            "Arrival of booking {BookingId} registered by the host (guest data complete: {GuestDataComplete})",
            booking.Id,
            guestDataComplete);
        return new StayArrivalResult(booking, guestDataComplete);
    }

    public async Task<Booking> StartCheckOutAsync(
        Guid bookingId,
        bool registerArrival,
        CancellationToken cancellationToken = default)
    {
        Booking booking;
        bool arrivalRegistered;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);
            var today = _clock.TodayInRome();
            if (StayLifecycleRules.CheckOutError(booking, today, registerArrival) is { } error)
                throw error;

            arrivalRegistered = booking.Status == BookingStatus.Confirmed;
            if (arrivalRegistered)
                MarkArrived(booking, today);

            booking.CheckoutWizardStartedAt ??= UtcNow();
            booking.UpdatedAt = UtcNow();
            if (arrivalRegistered)
            {
                // The host may leave the wizard here: the stay is checked in and gets its reminder like any other.
                await SaveWithReminderAsync(booking, transaction, cancellationToken);
            }
            else
            {
                await db.SaveChangesAsync(cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
            }
        }

        if (arrivalRegistered)
        {
            await ScheduleAlloggiatiAsync(booking.Id);
            logger.LogInformation("Arrival of booking {BookingId} registered with the start of the check-out", booking.Id);
        }

        return booking;
    }

    public async Task<Booking> CheckOutAsync(
        Guid bookingId,
        StayCheckOut checkOut,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkOut);

        Booking booking;
        string? reminderJobId;
        bool arrivalRegistered;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);
            var today = _clock.TodayInRome();
            if (StayLifecycleRules.CheckOutError(booking, today, checkOut.RegisterArrival) is { } error)
                throw error;

            arrivalRegistered = booking.Status == BookingStatus.Confirmed;
            if (arrivalRegistered)
                MarkArrived(booking, today);

            // Joins this transaction (same context): the request and the check-out are saved together or not at all.
            if (checkOut.Turnover is not null)
                await CreateTurnoverRequestAsync(booking, checkOut.Turnover, cancellationToken);

            reminderJobId = booking.CheckoutReminderJobId;
            booking.Status = BookingStatus.CheckedOut;
            booking.CheckoutReminderJobId = null;
            booking.UpdatedAt = UtcNow();
            await ExtendGuestRetentionAsync(booking, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }

        checkoutReminderScheduler.CancelReminder(reminderJobId);
        logger.LogInformation(
            "Booking {BookingId} checked out by the host (arrival registered with the check-out: {ArrivalRegistered})",
            booking.Id,
            arrivalRegistered);
        return booking;
    }

    /// <summary>
    /// Checked in now. The arrival instant is recorded only on the check-in day: registered later, the real arrival time
    /// is unknown, and the Alloggiati term keeps running from the start of the check-in day (never later than the legal
    /// one, <see cref="Casazen.Core.Regulatory.AlloggiatiTerms"/>).
    /// </summary>
    private void MarkArrived(Booking booking, DateTime todayInRome)
    {
        booking.Status = BookingStatus.CheckedIn;
        if (booking.ArrivedAt is null && booking.CheckInDate.Date == todayInRome.Date)
            booking.ArrivedAt = UtcNow();
        booking.UpdatedAt = UtcNow();
    }

    /// <summary>
    /// Schedules the check-out reminder of a stay just checked in (at <c>Compliance:CheckoutReminderHourLocal</c> of
    /// the check-out day, property time; in 5 minutes when that time has passed), then saves and commits. The job is
    /// deleted when the save fails.
    /// </summary>
    private async Task SaveWithReminderAsync(
        Booking booking,
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var jobId = checkoutReminderScheduler.ScheduleReminder(booking.Id, CheckoutReminderAt(booking));
        booking.CheckoutReminderJobId = jobId;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            checkoutReminderScheduler.CancelReminder(jobId);
            throw;
        }
    }

    private DateTime CheckoutReminderAt(Booking booking)
    {
        var reminderAtLocal = booking.CheckOutDate.Date.AddHours(complianceOptions.Value.CheckoutReminderHourLocal);
        var propertyZone = booking.Property.Timezone;
        var zone = !string.IsNullOrWhiteSpace(propertyZone) && TimezoneHelper.IsValidTimezone(propertyZone)
            ? propertyZone
            : RomeCalendar.TimeZoneId;
        var reminderAt = TimezoneHelper.ConvertLocalToUtc(reminderAtLocal, zone);
        var now = UtcNow();
        return reminderAt <= now ? now.AddMinutes(5) : reminderAt;
    }

    /// <summary>
    /// Idempotent per booking and guest: when the guest portal already scheduled the report, nothing is queued again.
    /// The arrival is already saved: a failure is logged, and from the arrival day the booking shows "to send manually"
    /// anyway (derived status, CO-11), so the host is never told it was sent.
    /// </summary>
    private async Task ScheduleAlloggiatiAsync(Guid bookingId)
    {
        try
        {
            await alloggiatiReportScheduler.EnsureScheduledAsync(bookingId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Alloggiati report of booking {BookingId} could not be scheduled at the arrival", bookingId);
        }
    }

    private async Task CreateTurnoverRequestAsync(
        Booking booking,
        StayTurnoverRequest turnover,
        CancellationToken cancellationToken)
    {
        try
        {
            await serviceRequestService.CreateAsync(new CreateServiceRequestCommand(
                booking.OrgId,
                turnover.UserId,
                booking.PropertyId,
                booking.Id,
                turnover.SupplierOrgId,
                string.IsNullOrWhiteSpace(turnover.Category) ? ServiceCategories.Cleaning : turnover.Category,
                ServiceRequestUrgency.Normal,
                turnover.Notes,
                ChargeToGuest: false), cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The service request rules (supplier active and covering the comune) still use InvalidOperationException:
            // the check-out is not saved and the host gets a 422 instead of a generic 500 (FD-05).
            logger.LogWarning(
                ex,
                "Turnover request of booking {BookingId} to supplier {SupplierOrgId} refused at check-out",
                booking.Id,
                turnover.SupplierOrgId);
            throw new DomainRuleException(BookingErrorCodes.TurnoverRequestInvalid, "CheckoutServiceRequestInvalid");
        }
    }

    /// <summary>
    /// The guest data are kept <c>Compliance:GdprRetentionYears</c> after the latest check-out of the guest's bookings,
    /// never less than already set.
    /// </summary>
    private async Task ExtendGuestRetentionAsync(Booking booking, CancellationToken cancellationToken)
    {
        var checkoutDates = await db.Bookings
            .AsNoTracking()
            .Where(b => b.GuestId == booking.GuestId && b.Status != BookingStatus.Cancelled)
            .Select(b => b.CheckOutDate)
            .ToListAsync(cancellationToken);
        var latestCheckout = checkoutDates.Count == 0 ? UtcNow() : checkoutDates.Max();
        var retentionUntil = latestCheckout.AddYears(complianceOptions.Value.GdprRetentionYears);

        if (retentionUntil > booking.Guest.DataRetentionUntil)
            booking.Guest.DataRetentionUntil = retentionUntil;
        booking.Guest.UpdatedAt = UtcNow();
    }

    private Task<IDbContextTransaction?> LockBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")));

    /// <summary>
    /// The booking as committed now, under the lock. The context may already track it (the controller read it for the
    /// authorization, before the lock): a query does not refresh a tracked entity, so it is reloaded, otherwise two
    /// transitions sent together would both see the old status.
    /// </summary>
    private async Task<Booking> LoadAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
        await db.Entry(booking).ReloadAsync(cancellationToken);
        return booking;
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;
}
