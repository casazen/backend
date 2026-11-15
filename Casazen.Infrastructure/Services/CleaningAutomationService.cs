using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ICleaningAutomationService" />
/// <remarks>
/// <list type="bullet">
/// <item><b>Which stays.</b> The ones that happen: the statuses and the no-show mark of <see cref="AlloggiatiStatusRules"/> (the one definition of a stay that is due), so a
/// request, a hold, a cancelled stay and a no-show get no cleaning. A property with no setup, or with an inactive one, is not automated.</item>
/// <item><b>Once for each stay.</b> The check, the request or the activity and the <see cref="StayCleaning"/> row that keys them are done under the advisory lock of the stay
/// and in one transaction (<see cref="PostgresAdvisoryLocks.Scope.CleaningAutomation"/>): a second call finds the row and creates nothing. The stay, the setup and the key are read
/// after the lock, and the two follow-ups of the changes of the stay take the same lock first: a change that comes while the cleaning is being made waits for it and finds its key. A cleaning someone else asked for
/// (a request or an activity of the stay that is not cancelled) counts as the cleaning of the stay: the automation never adds a second one.</item>
/// <item><b>The supplier path</b> is the request of the host, with every check it has (the supplier active and covering the comune, the supplier's calendar, the backups and the
/// timeouts under their flags): the time is the check-out day at the time of the setup on the clock of the property, the length the one of the setup, the price the agreed one
/// (its estimated amount) with the note «prezzo concordato». <b>The staff path</b> is a staff activity of type Cleaning assigned to the member, at the same time, with its cost
/// as the internal rate (none when the setup has no price) and the recharge to the owner, always: a cost entered later is recharged too. The member is checked again: one deactivated, or who lost the property, gets nothing.</item>
/// <item><b>When it cannot be created</b> (a supplier suspended or busy, a member who left) nothing is saved and the outcome is <see cref="CleaningAutomationOutcome.Failed"/> with
/// the code of the rule that stopped it; the host is told at once: the "To do" of a stay that ended without a cleaning (<see cref="ActivityType.CleaningDue"/>, the item the host
/// already knows) and a popup alert to the people of the property. An unexpected error (a database error, a timeout) is a failure too, with the code
/// <see cref="CleaningAutomationCodes.CreationFailed"/>, and the host is told the same way: the routine engine runs a routine once for each event, so a delivery of the event
/// again would run nothing. Only a cancelled call is not a failure of the cleaning: it goes on as the cancellation it is.</item>
/// <item><b>Changes of the stay</b> move the cleaning (a staff activity not started; a request nobody answered, with no backup, no proposal and no quote), or leave it to the
/// host (a "To do" <see cref="ActivityType.CleaningDateChanged"/> and an alert) when the supplier took it or has an offer waiting; a cancelled stay cancels it unless the work
/// started, and the supplier is told by the notice of a cancellation. A cleaning started, done or cancelled is left alone. The request they act on is the one the host follows:
/// when a backup supplier took the job in place of the primary (SP-BKP) it is the backup, not the primary the key points at.</item>
/// <item>Behind <c>Features:Routines</c>; the org is the caller's in every query; no personal data in the logs, the notes or the alerts.</item>
/// </list>
/// </remarks>
public sealed class CleaningAutomationService(
    AppDbContext db,
    IServiceRequestService serviceRequests,
    IInternalActivityService activities,
    IAlertRouter alertRouter,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<CleaningAutomationService> logger) : ICleaningAutomationService
{
    /// <summary>Key of the note of the request for the supplier in <c>EmailTexts</c> (Italian and English): «prezzo concordato».</summary>
    internal const string RequestNoteKey = "CleaningAutomation_RequestNote";

    /// <summary>Key of the note of the staff activity in <c>EmailTexts</c> (Italian and English).</summary>
    internal const string StaffNoteKey = "CleaningAutomation_StaffNote";

    /// <summary>Key of the reason of the cancellation in <c>EmailTexts</c> (Italian and English): «prenotazione annullata».</summary>
    internal const string CancelReasonKey = "CleaningAutomation_CancelReason";

    /// <summary>Key of the reason of the «To do» about a cleaning the host has to decide on, in <c>EmailTexts</c> (Italian and English).</summary>
    internal const string DateChangedReasonKey = "TodoReason_CleaningDateChanged";

    /// <summary>The user the log of a request made by the automation names: nobody signed in.</summary>
    internal const string SystemUserId = "system:cleaning-automation";

    /// <summary>The stay and the property facts the automation reads, never the guest or the prices.</summary>
    private sealed record StayFacts(
        Guid BookingId,
        Guid PropertyId,
        BookingStatus Status,
        DateTime? NoShowAt,
        DateTime CheckOutDate,
        string? Timezone,
        TimeOnly? CheckOutBy,
        RentalMode RentalMode,
        string OwnerId,
        string? ResponsibleUserId,
        string? Currency);

    internal static (PostgresAdvisoryLocks.Scope Scope, string Key) StayLock(Guid bookingId) =>
        (PostgresAdvisoryLocks.Scope.CleaningAutomation, bookingId.ToString("N"));

    public async Task<CleaningAutomationResult> CreateForStayAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.Routines))
            return new CleaningAutomationResult(CleaningAutomationOutcome.ModuleOff);

        StayFacts stay;
        PropertyCleaningSetup setup;
        CleaningAutomationResult? done = null;
        string? failureCode = null;

        // The lock of the stay comes first and everything is read under it: whoever held it before (another trigger of the stay, a change of its dates, its cancellation) has
        // committed, so the stay, the setup and the key are judged on what is there now, not on what they were when the event was handled.
        await using (var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, StayLock(bookingId)))
        {
            var readStay = await ReadStayAsync(orgId, bookingId, cancellationToken);
            if (readStay is null)
                return new CleaningAutomationResult(CleaningAutomationOutcome.Failed, CleaningAutomationCodes.StayNotFound);

            if (!IsDue(readStay))
                return new CleaningAutomationResult(CleaningAutomationOutcome.StayNotActive);

            var readSetup = await db.PropertyCleaningSetups
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == readStay.PropertyId && s.Active, cancellationToken);
            if (readSetup is null)
                return new CleaningAutomationResult(CleaningAutomationOutcome.NotConfigured);

            stay = readStay;
            setup = readSetup;

            var existing = await db.StayCleanings
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.OrgId == orgId && c.BookingId == bookingId, cancellationToken);
            if (existing is not null)
                return new CleaningAutomationResult(CleaningAutomationOutcome.AlreadyCreated, null, existing.ServiceRequestId, existing.InternalActivityId);

            if (await IsAlreadyPlannedAsync(orgId, bookingId, cancellationToken))
                return new CleaningAutomationResult(CleaningAutomationOutcome.AlreadyPlanned);

            try
            {
                done = setup.CleanerKind == PropertyCleanerKind.Supplier
                    ? await CreateRequestAsync(orgId, stay, setup, cancellationToken)
                    : await CreateActivityAsync(orgId, stay, setup, cancellationToken);
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);
            }
            catch (DomainException ex)
            {
                failureCode = ex.Code;
            }
            catch (NotFoundException ex)
            {
                failureCode = ex.Code ?? "not_found";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Not a rule: a database error, a timeout, a queue that refuses. It is a failure of the cleaning like any other, told to the host below, and it never waits for the bus to
                // deliver the event again: the routine engine runs a routine once for each event (its execution is kept even when it failed), so a second delivery would run nothing and
                // the stay would stay without a cleaning and without a word. Only the type of the error is logged (its message may carry data).
                logger.LogError(
                    "The automatic cleaning of stay {BookingId} of org {OrgId} failed on an unexpected error ({Error})",
                    bookingId, orgId, ex.GetType().Name);
                failureCode = CleaningAutomationCodes.CreationFailed;
            }
        }

        if (failureCode is not null)
        {
            // Nothing is left of the attempt (the transaction is gone, or the rule refused before any write): what it added to the unit of work is forgotten, so the save of the
            // "To do" below is not a second try of the creation. The host is told, and the event is consumed: a retry would be refused the same way.
            DiscardPendingChanges();
            logger.LogWarning(
                "No automatic cleaning for stay {BookingId} of org {OrgId}: {Code}",
                bookingId, orgId, failureCode);
            if (!await BookingTodoItems.WasRaisedAsync(db, orgId, ActivityType.CleaningDue, bookingId, cancellationToken))
            {
                await BookingCleaningActivityHandler.RaiseCleaningDueAsync(
                    db, activities, timeProvider, logger, orgId, bookingId, stay.PropertyId, stay.CheckOutDate, cancellationToken);
            }

            await AlertAsync(orgId, stay, deadline: null, cancellationToken);
            return new CleaningAutomationResult(CleaningAutomationOutcome.Failed, failureCode);
        }

        // The "To do" of a stay that ended without a cleaning (raised by the departure the host registered before the check-out time) has its cleaning now.
        await CloseCleaningDueAsync(orgId, bookingId, cancellationToken);

        logger.LogInformation(
            "Automatic cleaning of stay {BookingId} of org {OrgId} created for the {Kind} of property {PropertyId}",
            bookingId, orgId, setup.CleanerKind, stay.PropertyId);
        return done!;
    }

    public async Task<CleaningAutomationResult> FollowStayDatesAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.Routines))
            return new CleaningAutomationResult(CleaningAutomationOutcome.ModuleOff);

        // Under the lock of the stay, like the creation: a change that comes while the cleaning is being made waits for it and finds its key. The key, the stay and the setup are read
        // after the lock, so the change is judged on what is there now. The services the change calls join the transaction; it is committed here.
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, StayLock(bookingId));

        var cleaning = await db.StayCleanings.FirstOrDefaultAsync(c => c.OrgId == orgId && c.BookingId == bookingId, cancellationToken);
        if (cleaning is null)
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        // A stay that does not happen is the business of its cancellation, not of a change of its dates.
        var stay = await ReadStayAsync(orgId, bookingId, cancellationToken);
        if (stay is null || !IsDue(stay))
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        var setup = await db.PropertyCleaningSetups
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrgId == orgId && s.PropertyId == stay.PropertyId, cancellationToken);
        var start = CleaningSchedule.StartUtc(stay.CheckOutDate, setup?.StartTime, stay.CheckOutBy, stay.Timezone);
        if (start == cleaning.ScheduledStartUtc)
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        var result = cleaning.CleanerKind == PropertyCleanerKind.Supplier
            ? await MoveRequestAsync(orgId, stay, cleaning, start, setup, cancellationToken)
            : await MoveActivityAsync(orgId, cleaning, start, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return result;
    }

    public async Task<CleaningAutomationResult> FollowStayCancellationAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken = default)
    {
        if (!featureFlags.IsEnabled(FeatureFlags.Routines))
            return new CleaningAutomationResult(CleaningAutomationOutcome.ModuleOff);

        // Under the lock of the stay, and everything read after it (see FollowStayDatesAsync).
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(db, cancellationToken, StayLock(bookingId));

        var cleaning = await db.StayCleanings.FirstOrDefaultAsync(c => c.OrgId == orgId && c.BookingId == bookingId, cancellationToken);
        if (cleaning is null)
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        // The event says the stay does not happen (cancelled, or marked as a no-show), but events come late and out of order: the stay is read as it is now, and what the event says is
        // done only if it is still true. A cancellation that was undone since (the stay is due again) cancels nothing.
        var stay = await ReadStayAsync(orgId, bookingId, cancellationToken);
        if (stay is not null && IsDue(stay))
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        var result = cleaning.CleanerKind == PropertyCleanerKind.Supplier
            ? await CancelRequestAsync(orgId, cleaning, cancellationToken)
            : await CancelActivityAsync(orgId, cleaning, cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        return result;
    }

    // ─── Creation ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The request of the host to the supplier of the setup, through the service of the host's own requests (every check it makes), then the row that keys the stay. Called
    /// with the lock of the stay held and the transaction open: the request joins it, and both are committed by the caller.
    /// </summary>
    private async Task<CleaningAutomationResult> CreateRequestAsync(Guid orgId, StayFacts stay, PropertyCleaningSetup setup, CancellationToken cancellationToken)
    {
        if (setup is not { SupplierOrgId: { } supplierOrgId, PriceCents: { } price })
            throw new DomainRuleException(CleaningAutomationCodes.SetupInvalid, "CleaningAutomationSetupInvalid");

        // The requests to the suppliers, their prices and their payments are in euro and nothing is converted: the price of the setup is in the currency of the property, so a property in
        // another currency (it changed since the setup was saved, or the row was written by hand) gets no request.
        var currency = StayCurrency.Normalize(stay.Currency);
        if (!string.Equals(currency, StayCurrency.Euro, StringComparison.Ordinal))
            throw new DomainRuleException(CleaningAutomationCodes.SupplierCurrencyUnsupported, "CleaningAutomationSupplierCurrencyUnsupported", currency);

        var start = CleaningSchedule.StartUtc(stay.CheckOutDate, setup.StartTime, stay.CheckOutBy, stay.Timezone);
        var request = await serviceRequests.CreateAsync(
            new CreateServiceRequestCommand(
                orgId,
                SystemUserId,
                stay.PropertyId,
                stay.BookingId,
                supplierOrgId,
                ServiceCategories.Cleaning,
                ServiceRequestUrgency.Normal,
                await BookingTodoItems.ReasonAsync(db, orgId, RequestNoteKey, cancellationToken),
                ChargeToGuest: false,
                ServiceRequestRentalContext.ShortRent,
                ServiceListingId: null,
                ScheduledStartUtc: start,
                DurationMinutes: CleaningSchedule.DurationOf(setup.DurationMinutes),
                UseBackupSuppliers: null,
                LeaseContractId: null,
                AgreedAmountCents: price),
            cancellationToken);

        await KeyAsync(orgId, stay, PropertyCleanerKind.Supplier, request.Id, null, start, cancellationToken);
        return new CleaningAutomationResult(CleaningAutomationOutcome.Created, null, ServiceRequestId: request.Id);
    }

    /// <summary>The staff activity of the member of the setup, then the row that keys the stay. Called with the lock held and the transaction open.</summary>
    private async Task<CleaningAutomationResult> CreateActivityAsync(Guid orgId, StayFacts stay, PropertyCleaningSetup setup, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(setup.StaffUserId))
            throw new DomainRuleException(CleaningAutomationCodes.SetupInvalid, "CleaningAutomationSetupInvalid");

        // A member deactivated since the setup was saved, or who lost the property, gets no work.
        if (!await PropertyStaffReach.CanBeGivenWorkAsync(db, orgId, stay.PropertyId, stay.RentalMode, setup.StaffUserId, cancellationToken))
            throw new DomainRuleException(CleaningAutomationCodes.StaffUnavailable, "CleaningAutomationStaffUnavailable");

        var start = CleaningSchedule.StartUtc(stay.CheckOutDate, setup.StartTime, stay.CheckOutBy, stay.Timezone);
        var activity = await activities.CreateActivityAsync(
            stay.PropertyId,
            setup.StaffUserId,
            InternalActivity.CleaningActivityType,
            start,
            internalRate: setup.PriceCents is { } cents ? cents / 100m : null,
            rechargeToOwner: true,
            stay.BookingId,
            await BookingTodoItems.ReasonAsync(db, orgId, StaffNoteKey, cancellationToken));

        await KeyAsync(orgId, stay, PropertyCleanerKind.Staff, null, activity.Id, start, cancellationToken);
        return new CleaningAutomationResult(CleaningAutomationOutcome.Created, null, InternalActivityId: activity.Id);
    }

    private async Task KeyAsync(
        Guid orgId,
        StayFacts stay,
        PropertyCleanerKind kind,
        Guid? serviceRequestId,
        Guid? internalActivityId,
        DateTime start,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        db.StayCleanings.Add(new StayCleaning
        {
            OrgId = orgId,
            BookingId = stay.BookingId,
            PropertyId = stay.PropertyId,
            CleanerKind = kind,
            ServiceRequestId = serviceRequestId,
            InternalActivityId = internalActivityId,
            ScheduledStartUtc = start,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The stay has a cleaning someone asked for: a request of category cleaning that is not cancelled or refused (what <c>BookingCleaningActivityHandler</c> reads as
    /// «requested») or a staff activity of type Cleaning that is not cancelled.
    /// </summary>
    private async Task<bool> IsAlreadyPlannedAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken)
    {
        var requested = await db.ServiceRequests
            .AsNoTracking()
            .AnyAsync(
                r => r.OrgId == orgId
                     && r.BookingId == bookingId
                     && r.Category == ServiceCategories.Cleaning
                     && r.Status != ServiceRequestStatus.Annullato
                     && r.Status != ServiceRequestStatus.Rifiutato,
                cancellationToken);

        return requested
               || await db.InternalActivities
                   .AsNoTracking()
                   .AnyAsync(
                       a => a.OrgId == orgId
                            && a.BookingId == bookingId
                            && a.Action == null
                            && a.ActivityType == InternalActivity.CleaningActivityType
                            && a.Status != InternalActivity.StatusCancelled,
                       cancellationToken);
    }

    // ─── Changes of the stay ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task<CleaningAutomationResult> MoveRequestAsync(
        Guid orgId,
        StayFacts stay,
        StayCleaning cleaning,
        DateTime start,
        PropertyCleaningSetup? setup,
        CancellationToken cancellationToken)
    {
        var request = await ReadRequestAsync(orgId, cleaning, cancellationToken);

        // Started, done, refused or cancelled: nothing to move.
        if (request is not { Status: ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico })
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        // Nobody answered, and nothing hangs on the old time (no backup that holds it, no time proposed, no quote waiting): the request moves.
        if (request is { Status: ServiceRequestStatus.Richiesto, GroupId: null, ProposedStartUtc: null, QuoteSentAt: null })
        {
            var length = request is { ScheduledStartUtc: { } begins, ScheduledEndUtc: { } ends } && ends > begins
                ? ends - begins
                : TimeSpan.FromMinutes(CleaningSchedule.DurationOf(setup?.DurationMinutes));
            try
            {
                await serviceRequests.RescheduleAsHostAsync(request.Id, orgId, start, start + length, cancellationToken);
            }
            catch (DomainException ex)
            {
                // The supplier is busy then, or the request changed meanwhile: the host decides.
                logger.LogInformation(
                    "Cleaning request {RequestId} of stay {BookingId} was not moved: {Code}",
                    request.Id, stay.BookingId, ex.Code);
                return await LeaveToTheHostAsync(orgId, stay, cleaning, request.Id, ex.Code, cancellationToken);
            }

            cleaning.ScheduledStartUtc = start;
            cleaning.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            return new CleaningAutomationResult(CleaningAutomationOutcome.Moved, null, ServiceRequestId: request.Id);
        }

        // Taken by the supplier, or with an offer waiting: the host decides what happens to a job somebody agreed to.
        return await LeaveToTheHostAsync(orgId, stay, cleaning, request.Id, code: null, cancellationToken);
    }

    private async Task<CleaningAutomationResult> MoveActivityAsync(Guid orgId, StayCleaning cleaning, DateTime start, CancellationToken cancellationToken)
    {
        var activity = await ReadActivityAsync(orgId, cleaning, tracked: true, cancellationToken);

        // Started, done or cancelled: nothing to move.
        if (activity is not { Status: InternalActivity.StatusPending })
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        // One save: the activity and the key move together.
        activity.ScheduledAt = start;
        cleaning.ScheduledStartUtc = start;
        cleaning.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return new CleaningAutomationResult(CleaningAutomationOutcome.Moved, null, InternalActivityId: activity.Id);
    }

    private async Task<CleaningAutomationResult> CancelRequestAsync(Guid orgId, StayCleaning cleaning, CancellationToken cancellationToken)
    {
        var request = await ReadRequestAsync(orgId, cleaning, cancellationToken);

        // Not started: the host's cancellation, with its reason; the supplier is told by the existing notice. Started, done or over: left alone.
        if (request is not { Status: ServiceRequestStatus.Richiesto or ServiceRequestStatus.PresoInCarico })
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        try
        {
            await serviceRequests.CancelAsHostAsync(
                request.Id,
                orgId,
                await BookingTodoItems.ReasonAsync(db, orgId, CancelReasonKey, cancellationToken),
                cancellationToken);
        }
        catch (DomainException ex)
        {
            // The supplier started the work, or the request changed, between the read and the cancellation: nothing to cancel any more.
            logger.LogInformation("Cleaning request {RequestId} of stay {BookingId} was not cancelled: {Code}", request.Id, cleaning.BookingId, ex.Code);
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged, ex.Code, ServiceRequestId: request.Id);
        }
        catch (NotFoundException)
        {
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged, ServiceRequestErrorCodes.NotFound, ServiceRequestId: request.Id);
        }

        return new CleaningAutomationResult(CleaningAutomationOutcome.Cancelled, null, ServiceRequestId: request.Id);
    }

    private async Task<CleaningAutomationResult> CancelActivityAsync(Guid orgId, StayCleaning cleaning, CancellationToken cancellationToken)
    {
        var activity = await ReadActivityAsync(orgId, cleaning, tracked: true, cancellationToken);
        if (activity is not { Status: InternalActivity.StatusPending })
            return new CleaningAutomationResult(CleaningAutomationOutcome.Unchanged);

        // Kept for the audit, never completed: no cost is read from it.
        activity.Status = InternalActivity.StatusCancelled;
        activity.Notes = await BookingTodoItems.ReasonAsync(db, orgId, CancelReasonKey, cancellationToken);
        cleaning.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return new CleaningAutomationResult(CleaningAutomationOutcome.Cancelled, null, InternalActivityId: activity.Id);
    }

    /// <summary>
    /// The cleaning stays on the old day and the host decides: a "To do" about the stay (one while it is open) due when the cleaning is, and an alert to the people of the
    /// property. The cleaning keeps the start it was planned for, so the next change of the stay is judged against it.
    /// </summary>
    private async Task<CleaningAutomationResult> LeaveToTheHostAsync(
        Guid orgId,
        StayFacts stay,
        StayCleaning cleaning,
        Guid? requestId,
        string? code,
        CancellationToken cancellationToken)
    {
        await activities.CreateAsync(
            new CreateTodoRequest
            {
                Type = ActivityType.CleaningDateChanged,
                Action = ActivityAction.ReviewBooking,
                EntityType = InternalActivity.BookingEntityType,
                EntityId = stay.BookingId,
                Urgency = ActivityUrgency.High,
                PropertyId = stay.PropertyId,
                Deadline = cleaning.ScheduledStartUtc,
                Reason = await BookingTodoItems.ReasonAsync(db, orgId, DateChangedReasonKey, cancellationToken),
            },
            cancellationToken);
        await AlertAsync(orgId, stay, cleaning.ScheduledStartUtc, cancellationToken);

        return new CleaningAutomationResult(CleaningAutomationOutcome.HostToDecide, code, requestId ?? cleaning.ServiceRequestId, cleaning.InternalActivityId);
    }

    // ─── Reads and helpers ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Forgets the rows a failed attempt added to, changed in or removed from the unit of work and never saved, or saved in the transaction that was rolled back: they are detached, so
    /// the next save writes only what the caller adds after it. What the routine engine tracks for its own execution is saved before the action runs, so it is never among them.
    /// </summary>
    private void DiscardPendingChanges()
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
            entry.State = EntityState.Detached;
    }

    /// <summary>The stay and its property, in the org: <c>null</c> when the booking is not the org's, or its property is gone.</summary>
    private Task<StayFacts?> ReadStayAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken) =>
        (from booking in db.Bookings.AsNoTracking()
         join property in db.Properties.AsNoTracking() on booking.PropertyId equals property.Id
         where booking.Id == bookingId && booking.OrgId == orgId && property.OrgId == orgId
         select new StayFacts(
             booking.Id,
             booking.PropertyId,
             booking.Status,
             booking.NoShowAt,
             booking.CheckOutDate,
             property.Timezone,
             property.CheckOutBy,
             property.RentalMode,
             property.OwnerId,
             property.ResponsibleUserId,
             property.Currency))
        .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The stay happens: the one definition of a stay that is due (<see cref="AlloggiatiStatusRules.ActiveBookingStatuses"/>, no no-show).</summary>
    private static bool IsDue(StayFacts stay) => AlloggiatiStatusRules.ActiveBookingStatuses.Contains(stay.Status) && stay.NoShowAt is null;

    /// <summary>
    /// The request the host follows for the cleaning, as the org owns it (never another org's, whatever id the key holds). The key points at the request the automation made, which
    /// is the primary of a group of backups when the supplier has some (SP-BKP): when a backup takes the job the primary is cancelled (<see cref="ServiceRequestGroupRole.ReplacedPrimary"/>)
    /// and the work is the backup's (<see cref="ServiceRequestGroupRole.PromotedBackup"/>), so the request to move or to cancel is the head of the group of the key's request: the
    /// promoted backup when there is one, else the primary, in the same org.
    /// </summary>
    private async Task<ServiceRequest?> ReadRequestAsync(Guid orgId, StayCleaning cleaning, CancellationToken cancellationToken)
    {
        if (cleaning.ServiceRequestId is not { } id)
            return null;

        var request = await db.ServiceRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, cancellationToken);
        if (request?.GroupId is not { } groupId)
            return request;

        var heads = await db.ServiceRequests
            .AsNoTracking()
            .Where(r => r.OrgId == orgId
                        && r.GroupId == groupId
                        && (r.GroupRole == ServiceRequestGroupRole.Primary || r.GroupRole == ServiceRequestGroupRole.PromotedBackup))
            .ToListAsync(cancellationToken);

        return heads.FirstOrDefault(r => r.GroupRole == ServiceRequestGroupRole.PromotedBackup)
               ?? heads.FirstOrDefault(r => r.GroupRole == ServiceRequestGroupRole.Primary)
               ?? request;
    }

    /// <summary>The staff activity of the cleaning, as the org owns it; the row is changed by the caller, which asks for it tracked.</summary>
    private async Task<InternalActivity?> ReadActivityAsync(Guid orgId, StayCleaning cleaning, bool tracked, CancellationToken cancellationToken)
    {
        if (cleaning.InternalActivityId is not { } id)
            return null;

        var query = db.InternalActivities.Where(a => a.Id == id && a.OrgId == orgId && a.Action == null);
        return await (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Closes the open "To do" of a stay that ended without a cleaning: the stay has its cleaning now. Closed by the system, once.</summary>
    private async Task CloseCleaningDueAsync(Guid orgId, Guid bookingId, CancellationToken cancellationToken)
    {
        var typeName = nameof(ActivityType.CleaningDue);
        var open = await db.InternalActivities
            .AsNoTracking()
            .Where(a => a.OrgId == orgId
                        && a.Action != null
                        && a.CompletedAt == null
                        && a.ActivityType == typeName
                        && a.EntityType == InternalActivity.BookingEntityType
                        && a.EntityId == bookingId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in open)
            await activities.CloseAsync(id, InternalActivity.ClosedBySystem, cancellationToken);
    }

    /// <summary>The alert of the stay to the people the notifications of the property go to (the person in charge while they reach it, the administrators), once for each while it is open.</summary>
    private async Task AlertAsync(Guid orgId, StayFacts stay, DateTime? deadline, CancellationToken cancellationToken)
    {
        var users = await HostNotificationAudience
            .UsersToTell(db, orgId, stay.PropertyId, stay.ResponsibleUserId, stay.OwnerId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        foreach (var userId in users.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            await alertRouter.RouteAlertAsync(
                AlertType.BookingNeedsReview,
                stay.BookingId,
                PopupAlert.BookingEntityType,
                userId,
                AlertUrgency.High,
                deadline,
                cancellationToken);
        }
    }
}
