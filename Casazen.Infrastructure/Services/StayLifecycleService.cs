using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Hangfire;
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
    TimeProvider? timeProvider = null,
    IDomainEventRecorder? domainEvents = null,
    IBackgroundJobClient? backgroundJobs = null,
    IBookingFlowSettingsService? flowSettings = null,
    IPushNotificationService? pushNotifications = null) : IStayLifecycleService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// True when the police communication of the booking went or is going (NF-45): an Alloggiati attempt sending, accepted or partly accepted,
    /// or a report sent with a receipt or declared sent by the host. The no-show is refused then, and the wizard does not offer it.
    /// </summary>
    internal static async Task<bool> IsAlloggiatiSentAsync(AppDbContext db, Guid bookingId, CancellationToken cancellationToken) =>
        await db.AlloggiatiSendAttempts.AnyAsync(
            a => a.BookingId == bookingId
                 && (a.Status == AlloggiatiAttemptStatus.Sending
                     || a.Status == AlloggiatiAttemptStatus.Accepted
                     || a.Status == AlloggiatiAttemptStatus.PartiallyAccepted),
            cancellationToken)
        || await db.AlloggiatiWebReports.AnyAsync(
            r => r.BookingId == bookingId
                 && (r.Status == AlloggiatiWebStatus.Inviato || r.Status == AlloggiatiWebStatus.InviatoManualmente),
            cancellationToken);

    public async Task<Booking> MarkNoShowAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        Booking booking;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);

            // Idempotent: a stay already marked keeps its first mark, and NO_SHOW is not queued again.
            if (booking.Status == BookingStatus.Confirmed && booking.NoShowAt is not null)
                return booking;

            var alloggiatiSent = await IsAlloggiatiSentAsync(db, booking.Id, cancellationToken);
            if (CheckInReadinessRules.NoShowError(booking, _clock.TodayInRome(), alloggiatiSent) is { } error)
                throw error;

            booking.NoShowAt = UtcNow();
            booking.UpdatedAt = UtcNow();

            // The mark, saved with it, once per booking (E12/F01/US01/T03): the fiscal documents of the stay follow it (what the guest is charged is the consideration).
            await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.BookingNoShowMarked, booking, BookingEventOrigin.Host, cancellationToken);
            await SaveAsync(transaction, cancellationToken);
        }

        QueueOctorateNoShow(booking);
        logger.LogInformation("Booking {BookingId} marked as a no-show by the host", booking.Id);
        return booking;
    }

    public Task<StayArrivalResult> RegisterArrivalAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        RegisterArrivalAsync(bookingId, BookingEventOrigin.Host, cancellationToken);

    public async Task<StayArrivalResult?> RegisterArrivalAutomaticallyAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RegisterArrivalAsync(bookingId, BookingEventOrigin.System, cancellationToken);
            PushFlowStep(result.Booking, BookingFlowAutomaticStep.Arrival);
            return result;
        }
        catch (DomainException ex)
        {
            // The host moved first (registered it, marked a no-show, cancelled), or the day is not the right one any more: nothing to do.
            logger.LogInformation("Automatic arrival of booking {BookingId} not registered: {Code}", bookingId, ex.Code);
            return null;
        }
    }

    private async Task<StayArrivalResult> RegisterArrivalAsync(Guid bookingId, BookingEventOrigin origin, CancellationToken cancellationToken)
    {
        Booking booking;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);
            var today = _clock.TodayInRome();
            if (StayLifecycleRules.ArrivalError(booking, today) is { } error)
                throw error;

            // The automation registers a stay the host did not mark a no-show: a mark made meanwhile wins.
            if (origin == BookingEventOrigin.System && booking.NoShowAt is not null)
                throw new DomainConflictException(BookingErrorCodes.NotConfirmed, "BookingArrivalNotConfirmed");

            await MarkArrivedAsync(booking, today, origin, cancellationToken);
            await SaveAsync(transaction, cancellationToken);
        }

        await ScheduleAlloggiatiAsync(booking.Id);
        var guestDataComplete = await alloggiatiWebService.IsStayDataCompleteAsync(booking.Id);
        logger.LogInformation(
            "Arrival of booking {BookingId} registered by the {Origin} (guest data complete: {GuestDataComplete})",
            booking.Id,
            origin == BookingEventOrigin.System ? "booking flow" : "host",
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
                await MarkArrivedAsync(booking, today, BookingEventOrigin.Host, cancellationToken);

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

    public Task<Booking> CheckOutAsync(
        Guid bookingId,
        StayCheckOut checkOut,
        CancellationToken cancellationToken = default) =>
        CheckOutAsync(bookingId, checkOut, BookingEventOrigin.Host, cancellationToken);

    public async Task<Booking?> CheckOutAutomaticallyAsync(Guid bookingId, bool registerArrival, CancellationToken cancellationToken = default)
    {
        try
        {
            var booking = await CheckOutAsync(bookingId, new StayCheckOut(registerArrival), BookingEventOrigin.System, cancellationToken);
            PushFlowStep(booking, BookingFlowAutomaticStep.Departure);
            return booking;
        }
        catch (DomainException ex)
        {
            // The host moved first (checked it out, opened the wizard, marked a no-show, cancelled): nothing to do.
            logger.LogInformation("Automatic departure of booking {BookingId} not registered: {Code}", bookingId, ex.Code);
            return null;
        }
    }

    public async Task<StayCheckout?> DeclarePropertyReadyAutomaticallyAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        Booking booking;
        StayCheckout record;
        await using (var transaction = await LockBookingAsync(bookingId, cancellationToken))
        {
            booking = await LoadAsync(bookingId, cancellationToken);
            if (booking.Status != BookingStatus.CheckedOut || flowSettings is null)
                return null;

            var flow = await flowSettings.GetAsync(booking.OrgId, cancellationToken);
            if (!flow.DeclaresReadyWhenCleaningDone)
                return null;

            record = await LoadOrCreateCheckoutAsync(booking, cancellationToken);
            // Declared already, or the host said «not yet» at the check-out (damages, missing items): the host declares it.
            if (record.PropertyReadyAt is not null || record.PropertyReady == false)
                return record;

            record.PropertyReady = true;
            record.PropertyReadyAt = UtcNow();
            record.CurrentStep = CheckoutWizardStep.PropertyReady;
            record.UpdatedAt = UtcNow();

            // The declaration closes the cleaning "To do" of the stay, as the host's does: its event is saved with it, once.
            await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.PropertyReadyDeclared, booking, BookingEventOrigin.System, cancellationToken);
            await SaveAsync(transaction, cancellationToken);
        }

        logger.LogInformation("Property of booking {BookingId} declared ready by the booking flow: the cleaning is done", bookingId);
        PushFlowStep(booking, BookingFlowAutomaticStep.PropertyReady);
        return record;
    }

    private async Task<Booking> CheckOutAsync(
        Guid bookingId,
        StayCheckOut checkOut,
        BookingEventOrigin origin,
        CancellationToken cancellationToken)
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

            // The automation never closes a stay the host is closing (the wizard is open) or marked a no-show.
            if (origin == BookingEventOrigin.System && (booking.CheckoutWizardStartedAt is not null || booking.NoShowAt is not null))
                throw new DomainConflictException(BookingErrorCodes.CheckoutWizardNotStarted, "CheckoutWizardNotStarted");

            arrivalRegistered = booking.Status == BookingStatus.Confirmed;
            if (arrivalRegistered)
                await MarkArrivedAsync(booking, today, origin, cancellationToken);

            // Joins this transaction (same context): the request and the check-out are saved together or not at all.
            var turnoverRequest = checkOut.Turnover is null
                ? null
                : await CreateTurnoverRequestAsync(booking, checkOut.Turnover, cancellationToken);

            // Checked out: the stay-alerts job no longer reminds it (CO-10).
            booking.Status = BookingStatus.CheckedOut;
            booking.CheckedOutAt = UtcNow();
            booking.UpdatedAt = UtcNow();
            // CO-15: retention is now computed nightly from the stay's check-out date, no eager extension here.
            var record = await LoadOrCreateCheckoutAsync(booking, cancellationToken);
            CloseCheckout(record, checkOut, turnoverRequest);

            // casazen/backend#978: a check-out that declares nothing (the endpoint, the automatic departure) leaves the property to the booking flow of the org, which
            // declares it ready now when it says so (at the check-out, or when the cleaning of the stay is already done). The wizard's answer is the host's and wins.
            var readyByFlow = record.PropertyReadyAt is null
                && checkOut.Wizard is null
                && record.PropertyReady != false
                && await IsReadyAtCheckOutAsync(booking, cancellationToken);
            if (readyByFlow)
            {
                record.PropertyReady = true;
                record.PropertyReadyAt = UtcNow();
                record.CurrentStep = CheckoutWizardStep.PropertyReady;
            }

            // The check-out, and the property declared ready at the same time when the wizard or the flow says so: saved with the check-out.
            await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.CheckOutCompleted, booking, origin, cancellationToken);
            if (record.PropertyReadyAt is not null)
                await BookingEvents.RecordAsync(
                    domainEvents, db, DomainEventType.PropertyReadyDeclared, booking, readyByFlow ? BookingEventOrigin.System : origin, cancellationToken);
            await SaveAsync(transaction, cancellationToken);
        }

        logger.LogInformation(
            "Booking {BookingId} checked out by the {Origin} (arrival registered with the check-out: {ArrivalRegistered})",
            booking.Id,
            origin == BookingEventOrigin.System ? "booking flow" : "host",
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

            // The declaration closes the cleaning "To do" of the stay: its event is saved with the declaration, once.
            await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.PropertyReadyDeclared, booking, BookingEventOrigin.Host, cancellationToken);
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
    private async Task MarkArrivedAsync(Booking booking, DateTime todayInRome, BookingEventOrigin origin, CancellationToken cancellationToken)
    {
        booking.Status = BookingStatus.CheckedIn;

        // A late arrival after a no-show (NF-45): the guests did come, the mark goes.
        booking.NoShowAt = null;
        if (booking.ArrivedAt is null && booking.CheckInDate.Date == todayInRome.Date)
            booking.ArrivedAt = UtcNow();
        booking.UpdatedAt = UtcNow();

        // The check-in of the stay, whichever way the arrival is registered: saved with it, once per booking (ADR-022-R1, R8).
        await BookingEvents.RecordAsync(domainEvents, db, DomainEventType.CheckInCompleted, booking, origin, cancellationToken);
    }

    /// <summary>
    /// Whether the booking flow of the org declares the property of <paramref name="booking"/> ready at its check-out (casazen/backend#978): always with the trigger
    /// «at the check-out»; with «when the cleaning is done» only when a cleaning of the stay is already done. No settings service (a test): never.
    /// </summary>
    private async Task<bool> IsReadyAtCheckOutAsync(Booking booking, CancellationToken cancellationToken)
    {
        if (flowSettings is null)
            return false;

        var flow = await flowSettings.GetAsync(booking.OrgId, cancellationToken);
        if (flow.DeclaresReadyAtCheckOut)
            return true;

        return flow.DeclaresReadyWhenCleaningDone && await StayCleaningState.IsDoneAsync(db, booking.OrgId, booking.Id, cancellationToken);
    }

    /// <summary>
    /// Tells the hosts of the booking, live, that CasaZen did a step of the stay in their place (bell, pop-up, the open page refreshed): best effort after the save, never
    /// undoing the step. The property only, never the guest.
    /// </summary>
    private void PushFlowStep(Booking booking, BookingFlowAutomaticStep step) =>
        BookingFlowLiveRefresh.Tell(pushNotifications, booking.Id, booking.Property?.Name, step, logger);

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

    /// <summary>
    /// After the commit, a booking on Octorate gets <c>NO_SHOW</c> through <see cref="OctorateStayStateJob"/> (which writes nothing for a booking
    /// that is not mirrored). The no-show is saved: a failure to queue is logged and never undoes it.
    /// </summary>
    private void QueueOctorateNoShow(Booking booking)
    {
        if (backgroundJobs is null || string.IsNullOrWhiteSpace(booking.OctorateReservationId))
            return;

        var orgId = booking.OrgId;
        var id = booking.Id;
        try
        {
            backgroundJobs.Enqueue<OctorateStayStateJob>(job => job.RunAsync(orgId, id, OctorateStayState.NoShow, CancellationToken.None));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The no-show of booking {BookingId} could not be queued for Octorate", id);
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
