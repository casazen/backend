using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Arrival and departure of a stay (CO-08, A5-08). See <see cref="IStayLifecycleService"/> and
/// <see cref="StayLifecycleRules"/>.
/// </summary>
/// <remarks>
/// Each transition takes the advisory lock of the booking used by the cancellation and the host changes (BK-02,
/// PC-07), reads the booking again under it, checks the rules and saves in the same transaction. The Alloggiati job is
/// scheduled after the commit (idempotent per booking and guest, CO-11). The check-out reminder is not scheduled here:
/// the hourly stay-alerts job derives it from every confirmed or checked-in stay (CO-10).
/// </remarks>
public sealed class StayLifecycleService(
    AppDbContext db,
    IAlloggiatiWebService alloggiatiWebService,
    IAlloggiatiReportScheduler alloggiatiReportScheduler,
    IServiceRequestService serviceRequestService,
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
            await SaveAsync(transaction, cancellationToken);
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
            // The progress of the wizard (CO-17): created once, kept when the wizard is opened again.
            await LoadOrCreateCheckoutAsync(booking, cancellationToken);
            await SaveAsync(transaction, cancellationToken);
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
        bool arrivalRegistered;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);
            var today = _clock.TodayInRome();
            if (StayLifecycleRules.CheckOutError(booking, today, checkOut.RegisterArrival) is { } error)
                throw error;

            // The wizard closes only what it opened (CO-17): its answers were given after the start.
            if (checkOut.Wizard is not null && booking.CheckoutWizardStartedAt is null)
                throw new DomainConflictException(BookingErrorCodes.CheckoutWizardNotStarted, "CheckoutWizardNotStarted");

            arrivalRegistered = booking.Status == BookingStatus.Confirmed;
            if (arrivalRegistered)
                MarkArrived(booking, today);

            // Joins this transaction (same context): the request and the check-out are saved together or not at all.
            var turnoverRequest = checkOut.Turnover is null
                ? null
                : await CreateTurnoverRequestAsync(booking, checkOut.Turnover, cancellationToken);

            // Checked out: the stay-alerts job no longer reminds it (CO-10).
            booking.Status = BookingStatus.CheckedOut;
            booking.UpdatedAt = UtcNow();
            // CO-15: retention is now computed nightly from the stay's check-out date, no eager extension here.
            var record = await LoadOrCreateCheckoutAsync(booking, cancellationToken);
            CloseCheckout(record, checkOut, turnoverRequest);
            await SaveAsync(transaction, cancellationToken);
        }

        logger.LogInformation(
            "Booking {BookingId} checked out by the host (arrival registered with the check-out: {ArrivalRegistered})",
            booking.Id,
            arrivalRegistered);
        return booking;
    }

    public async Task<StayCheckout> SaveCheckoutProgressAsync(
        Guid bookingId,
        StayCheckoutProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        // Only category codes are kept (SU-03): an unknown one is a 422 now, not at the check-out.
        var skipped = progress.CleaningChoice == CheckoutCleaningChoice.Skip;
        var category = skipped || string.IsNullOrWhiteSpace(progress.CleaningCategory)
            ? null
            : ServiceCategories.Require(progress.CleaningCategory);

        StayCheckout record;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            var booking = await LoadAsync(bookingId, cancellationToken);
            if (booking.Status == BookingStatus.CheckedOut)
                throw new DomainConflictException(BookingErrorCodes.AlreadyCheckedOut, "BookingAlreadyCheckedOut");
            if (booking.CheckoutWizardStartedAt is null || booking.Status != BookingStatus.CheckedIn)
                throw new DomainConflictException(BookingErrorCodes.CheckoutWizardNotStarted, "CheckoutWizardNotStarted");

            record = await LoadOrCreateCheckoutAsync(booking, cancellationToken);
            record.CurrentStep = progress.CurrentStep;
            record.DepartureConfirmed = progress.DepartureConfirmed;
            record.CleaningChoice = progress.CleaningChoice;
            record.CleaningSupplierOrgId = skipped ? null : progress.CleaningSupplierOrgId;
            record.CleaningCategory = category;
            record.CleaningNotes = skipped ? null : Trimmed(progress.CleaningNotes);
            record.TouristTaxCollection = progress.TouristTaxCollection;
            record.PropertyReady = progress.PropertyReady;
            record.PropertyNotes = Trimmed(progress.PropertyNotes);
            record.UpdatedAt = UtcNow();
            await SaveAsync(transaction, cancellationToken);
        }

        return record;
    }

    public async Task<StayCheckout> ConfirmPropertyReadyAsync(
        Guid bookingId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        StayCheckout record;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            var booking = await LoadAsync(bookingId, cancellationToken);
            if (booking.Status != BookingStatus.CheckedOut)
                throw new DomainConflictException(BookingErrorCodes.CheckoutNotCompleted, "CheckoutNotCompleted");

            // A stay closed before CO-17 has no row yet: the declaration creates it (its check-out time is unknown).
            record = await LoadOrCreateCheckoutAsync(booking, cancellationToken);
            if (record.PropertyReadyAt is not null)
                return record;

            record.PropertyReady = true;
            record.PropertyReadyAt = UtcNow();
            if (Trimmed(notes) is { } trimmed)
                record.PropertyNotes = trimmed;
            record.CurrentStep = CheckoutWizardStep.PropertyReady;
            record.UpdatedAt = UtcNow();
            await SaveAsync(transaction, cancellationToken);
        }

        logger.LogInformation("Property of booking {BookingId} declared ready after the check-out", bookingId);
        return record;
    }

    /// <summary>
    /// Closes the check-out record with the check-out. From the wizard it records what the host declared; from
    /// <c>POST /check-out</c> nothing is declared, so the property is not ready until the host says so (cockpit).
    /// </summary>
    private void CloseCheckout(StayCheckout record, StayCheckOut checkOut, ServiceRequest? turnoverRequest)
    {
        var now = UtcNow();
        record.CompletedAt = now;
        record.UpdatedAt = now;

        if (turnoverRequest is not null && checkOut.Turnover is { } turnover)
        {
            record.CleaningChoice = CheckoutCleaningChoice.Request;
            record.CleaningSupplierOrgId = turnover.SupplierOrgId;
            record.CleaningCategory = turnoverRequest.Category;
            record.CleaningNotes = Trimmed(turnover.Notes);
            record.CleaningRequestId = turnoverRequest.Id;
        }

        if (checkOut.Wizard is not { } wizard)
            return;

        record.DepartureConfirmed = true;
        record.CurrentStep = CheckoutWizardStep.PropertyReady;
        if (turnoverRequest is null)
        {
            record.CleaningChoice = wizard.CleaningSkipped ? CheckoutCleaningChoice.Skip : null;
            record.CleaningSupplierOrgId = null;
            record.CleaningCategory = null;
            record.CleaningNotes = null;
        }

        record.TouristTaxCollection = wizard.TouristTaxCollection;
        record.PropertyReady = wizard.PropertyReady;
        record.PropertyNotes = Trimmed(wizard.PropertyNotes);
        record.PropertyReadyAt = wizard.PropertyReady ? now : null;
    }

    /// <summary>The check-out record of the booking, created (not saved) when missing. Called under the booking lock.</summary>
    private async Task<StayCheckout> LoadOrCreateCheckoutAsync(Booking booking, CancellationToken cancellationToken)
    {
        var record = await db.StayCheckouts.FirstOrDefaultAsync(c => c.BookingId == booking.Id, cancellationToken);
        if (record is not null)
        {
            // Tracked since an earlier read of this context: the committed row may have changed before the lock.
            await db.Entry(record).ReloadAsync(cancellationToken);
            return record;
        }

        var now = UtcNow();
        record = new StayCheckout
        {
            BookingId = booking.Id,
            OrgId = booking.OrgId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.StayCheckouts.Add(record);
        return record;
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    private async Task SaveAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
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

    private async Task<ServiceRequest> CreateTurnoverRequestAsync(
        Booking booking,
        StayTurnoverRequest turnover,
        CancellationToken cancellationToken)
    {
        try
        {
            return await serviceRequestService.CreateAsync(new CreateServiceRequestCommand(
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
        catch (Exception ex) when (ex is NotFoundException
            or DomainRuleException { Code: ServiceRequestErrorCodes.SupplierInactive or ServiceRequestErrorCodes.SupplierOutsideComune })
        {
            // The supplier of the turnover request is missing, not active or not covering the comune (SU-10 typed
            // errors): the check-out is not saved and the host gets the check-out's own 422 (FD-05).
            logger.LogWarning(
                ex,
                "Turnover request of booking {BookingId} to supplier {SupplierOrgId} refused at check-out",
                booking.Id,
                turnover.SupplierOrgId);
            throw new DomainRuleException(BookingErrorCodes.TurnoverRequestInvalid, "CheckoutServiceRequestInvalid");
        }
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
