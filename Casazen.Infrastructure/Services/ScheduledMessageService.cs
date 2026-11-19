using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Scheduling;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IScheduledMessageService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>Nothing by default.</b> A row is written only from an active routine that holds a <c>MessageToGuest</c>; an organization with no such routine has none. A message whose moment passed when it would be
/// planned (by more than the catch-up time) is not planned: activating a routine never sends a welcome message to a guest who was welcomed long ago. A planned message that moves into the past becomes <c>Expired</c>.</item>
/// <item><b>Once.</b> The unique index of the plan keeps a booking, a routine and an action to one row; a row written by two planners at the same moment is one (the loser finds the index and does nothing). A message
/// is sent by the one dispatcher (<see cref="IScheduledMessageDispatcher"/>), which claims the row first.</item>
/// <item><b>Who may do what.</b> The scope is applied in SQL to the booking and to the row (<c>InScope</c>), and the org of the scope is named in every query: a row of another org or out of the reach is a 404. The
/// planning and the engine work in the org of the booking or the routine, with no caller.</item>
/// <item><b>Personal data.</b> A preview carries the words of the message to the host who may read the booking, and nothing else of the guest; the logs carry ids, states and codes.</item>
/// </list>
/// </remarks>
public sealed class ScheduledMessageService(
    AppDbContext db,
    IScheduledMessageDispatcher dispatcher,
    IMessageReplyService replies,
    IInternalActivityService activities,
    ScheduledMessageAlertDelivery alerts,
    IConditionEvaluator conditions,
    IOptions<ScheduledMessageOptions> options,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<ScheduledMessageService> logger) : IScheduledMessageService
{
    private const string Tenant = AppDbContext.TenantQueryFilter;

    private const string DefaultZone = "Europe/Rome";

    // The messages are the work of the routines: with the module of the routines closed nothing is planned or sent, whatever the flag of the messages says.
    private bool Enabled => featureFlags.IsEnabled(FeatureFlags.ScheduledMessages) && featureFlags.IsEnabled(FeatureFlags.Routines);

    public async Task<int> PlanBookingAsync(Guid bookingId, CancellationToken ct = default)
    {
        if (!Enabled)
            return 0;

        var now = Now();
        var settings = options.Value;
        var stay = await LoadStayAsync(bookingId, ct);
        if (stay is null)
            return 0;

        var routines = await db.Routines
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(r => r.OrgId == stay.OrgId)
            .Select(r => new RoutineRow(new PlanningRoutine(r.Id, r.Name, r.EventType, r.EventConfig, r.ActionsJson, r.Active, r.TestMode), r.ConditionJson))
            .ToListAsync(ct);
        var rows = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .Where(m => m.BookingId == bookingId && m.OrgId == stay.OrgId)
            .ToListAsync(ct);

        var gone = now - TimeSpan.FromMinutes(settings.EffectiveCatchUpMinutes);
        var changed = 0;
        var kept = new HashSet<(Guid RoutineId, int ActionIndex)>();
        var notMet = new HashSet<Guid>();
        var facts = RoutineConditionFacts.OfBooking(stay.PropertyId, stay.Source, stay.Planning.CheckInDate, stay.Planning.CheckOutDate, stay.NumberOfGuests, stay.Planning.CreatedAt);
        foreach (var entry in routines)
        {
            var routine = entry.Planning;
            var plan = ScheduledMessagePlanner.Plan(routine, stay.Planning, settings);
            if (plan.Count == 0)
                continue;

            // The condition of the routine, read as the engine reads it when the routine fires: a stay that does not meet it gets no message, and one that stopped meeting it loses the ones it had.
            if (!await ScheduledMessageConditions.HoldsAsync(conditions, stay.OrgId, routine.Id, entry.ConditionJson, facts, logger, ct))
            {
                notMet.Add(routine.Id);
                continue;
            }

            foreach (var planned in plan)
            {
                kept.Add((routine.Id, planned.ActionIndex));
                var row = rows.FirstOrDefault(r => r.RoutineId == routine.Id && r.ActionIndex == planned.ActionIndex);
                if (row is null)
                {
                    // Its moment passed before it was planned (the routine was activated after it, the booking was confirmed after it): it is not sent late.
                    if (planned.PlannedAtUtc < gone)
                        continue;

                    // DbSet.Add: the row carries its own Guid, so only Add tracks it as new.
                    db.ScheduledMessages.Add(NewRow(stay, routine, planned, now));
                    changed++;
                    continue;
                }

                changed += Follow(row, routine, planned, gone, now);
            }
        }

        // What no routine plans any more (switched off, deleted, the action gone, a stay that does not happen, or one that does not meet the condition of the routine) does not go out.
        var stayHappens = stay.Planning.Status is not BookingStatus.Cancelled && !stay.Planning.NoShow && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(stay.Planning.Status);
        foreach (var row in rows.Where(r => r.IsOpen && !(r.RoutineId is { } routineId && kept.Contains((routineId, r.ActionIndex)))))
        {
            var reason = !stayHappens
                ? ScheduledMessageReasons.BookingCancelled
                : row.RoutineId is { } held && notMet.Contains(held) ? ScheduledMessageReasons.ConditionNotMet : ScheduledMessageReasons.RoutineInactive;
            Close(row, ScheduledMessageStatus.Cancelled, reason, now);
            changed++;
        }

        if (changed == 0)
            return 0;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsPlanConflict(ex))
        {
            // Another planner wrote the same row at the same moment: the database kept the first one, and the next pass looks again.
            db.ChangeTracker.Clear();
            logger.LogInformation("Scheduled messages of booking {BookingId} (org {OrgId}): another run planned them at the same moment", bookingId, stay.OrgId);
            return 0;
        }

        await CloseTodoIfIdleAsync(stay.OrgId, bookingId, Guid.Empty, ct);
        logger.LogInformation("Scheduled messages of booking {BookingId} (org {OrgId}): {Changed} rows written or changed", bookingId, stay.OrgId, changed);
        return changed;
    }

    public async Task<int> CancelForBookingAsync(Guid bookingId, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!Enabled)
            return 0;

        var now = Now();

        // What the event says is done only if the stay is still so: a cancellation that was undone since (a booking confirmed again) cancels nothing, so events that arrive out of order give the same result.
        var stay = await LoadStayAsync(bookingId, ct);
        if (stay is not null && !stay.Planning.NoShow && AlloggiatiStatusRules.ActiveBookingStatuses.Contains(stay.Planning.Status))
            return 0;

        var open = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .Where(m => m.BookingId == bookingId && (m.Status == ScheduledMessageStatus.Scheduled || m.Status == ScheduledMessageStatus.Blocked || m.Status == ScheduledMessageStatus.Failed))
            .ToListAsync(ct);
        if (open.Count == 0)
            return 0;

        foreach (var row in open)
            Close(row, ScheduledMessageStatus.Cancelled, reason, now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A message was claimed by a send meanwhile: the dispatch checks the booking before it sends, and the next event looks again.
            db.ChangeTracker.Clear();
            return 0;
        }

        await CloseTodoIfIdleAsync(open[0].OrgId, bookingId, Guid.Empty, ct);
        logger.LogInformation("Scheduled messages of booking {BookingId} (org {OrgId}): {Count} cancelled ({Reason})", bookingId, open[0].OrgId, open.Count, reason);
        return open.Count;
    }

    public async Task<int> ReplanOrgAsync(Guid orgId, CancellationToken ct = default)
    {
        if (!Enabled)
            return 0;

        var settings = options.Value;
        var today = RomeCalendar.TodayAt(new DateTimeOffset(Now()));
        var horizon = today.AddDays(settings.EffectivePlanHorizonDays);
        var from = today.AddDays(-1);
        var active = AlloggiatiStatusRules.ActiveBookingStatuses;
        var open = new[] { ScheduledMessageStatus.Scheduled, ScheduledMessageStatus.Blocked, ScheduledMessageStatus.Failed };

        // The stays that are close, and the stays that still have a message to settle (a cancelled one has its messages cancelled by the plan).
        var near = await db.Bookings
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(b => b.OrgId == orgId && active.Contains(b.Status) && b.NoShowAt == null && b.CheckOutDate >= from && b.CheckInDate <= horizon)
            .Select(b => b.Id)
            .ToListAsync(ct);
        var settling = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(m => m.OrgId == orgId && open.Contains(m.Status))
            .Select(m => m.BookingId)
            .Distinct()
            .ToListAsync(ct);

        var changed = 0;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
        foreach (var bookingId in near.Union(settling))
        {
            ct.ThrowIfCancellationRequested();

            // One booking at a time: nothing of the previous one stays in the change tracker.
            db.ChangeTracker.Clear();
            try
            {
                changed += await PlanBookingAsync(bookingId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The scheduled messages of booking {BookingId} (org {OrgId}) could not be planned", bookingId, orgId);
                failure ??= System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
        return changed;
    }

    public async Task<IReadOnlyList<Guid>> OrgsWithMessageRoutinesAsync(CancellationToken ct = default)
    {
        var open = new[] { ScheduledMessageStatus.Scheduled, ScheduledMessageStatus.Blocked, ScheduledMessageStatus.Failed };

        // An organization with an active routine that sends a message; and one that still has an open message, so a routine switched off or deleted cancels what it planned.
        var withRoutines = await db.Routines
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(r => r.Active && !r.TestMode && r.ActionsJson.Contains(nameof(RoutineActionType.MessageToGuest)))
            .Select(r => r.OrgId)
            .Distinct()
            .ToListAsync(ct);
        var withMessages = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(m => open.Contains(m.Status))
            .Select(m => m.OrgId)
            .Distinct()
            .ToListAsync(ct);
        return [.. withRoutines.Union(withMessages)];
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The timeline and what the host does with it
    // ------------------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ScheduledMessageView>> ListForBookingAsync(HostScope scope, Guid bookingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await ViewsAsync(scope, bookingId, null, ct);
    }

    public async Task<Guid?> FindBookingIdAsync(HostScope scope, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await db.ScheduledMessages
            .AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.Id == id)
            .InScope(scope, m => m.Property)
            .Select(m => (Guid?)m.BookingId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<ScheduledMessageView> EditAsync(HostScope scope, Guid id, string? text, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var row = await OwnRowAsync(scope, id, ct);
        if (!row.IsOpen)
            throw new DomainConflictException(ScheduledMessageErrors.NotEditable, ScheduledMessageErrors.NotEditableMessageKey);

        // The same rules as the composer of the hub: an empty text goes back to the template; a text that is too long is 422; a name that is not a variable is 422.
        string? clean = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            clean = GuestMessageRules.NormalizeText(text);
            if (clean is null || clean.Length > GuestMessageRules.TextMaxLength)
                throw new DomainRuleException(MessageChannelRules.TextInvalidCode, "GuestMessageTextInvalid");

            MessageTemplateRenderer.VariablesOf(clean);
        }

        row.TextOverride = clean;
        row.UpdatedAt = Now();
        await SaveOwnAsync(ct);

        logger.LogInformation("Scheduled message {MessageId} of org {OrgId} edited", id, scope.OrgId);
        return (await ViewsAsync(scope, row.BookingId, id, ct)).Single();
    }

    public async Task<ScheduledMessageView> SkipAsync(HostScope scope, Guid id, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var row = await OwnRowAsync(scope, id, ct);
        if (!row.IsOpen)
            throw new DomainConflictException(ScheduledMessageErrors.NotEditable, ScheduledMessageErrors.NotEditableMessageKey);

        var now = Now();
        row.Status = ScheduledMessageStatus.Skipped;
        row.SkipReason = ScheduledMessageReasons.Person;
        row.SkippedByUserId = userId.Length <= ScheduledMessage.UserIdMaxLength ? userId : userId[..ScheduledMessage.UserIdMaxLength];
        row.SkippedAt = now;
        row.ActivityId = null;
        row.LastCheckedAt = null;
        row.UpdatedAt = now;
        await SaveOwnAsync(ct);

        logger.LogInformation("Scheduled message {MessageId} of org {OrgId} skipped by a person", id, scope.OrgId);
        await CloseTodoIfIdleAsync(row.OrgId, row.BookingId, row.Id, ct);

        // Nobody needs an alert about their own action: the OTHER people who reach the property are told (AC-NF71-3).
        try
        {
            await alerts.AlertOrRetryAsync(new ScheduledMessageAlert(row.OrgId, row.PropertyId, row.BookingId, row.Id, ScheduledMessageAlertKinds.Skipped, userId, now), CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The alert about the skipped scheduled message {MessageId} of org {OrgId} could not even be queued", id, scope.OrgId);
        }

        return (await ViewsAsync(scope, row.BookingId, id, ct)).Single();
    }

    public async Task<ScheduledMessageView> SendNowAsync(HostScope scope, Guid id, string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var row = await OwnRowAsync(scope, id, ct);
        if (!row.IsOpen)
            throw new DomainConflictException(ScheduledMessageErrors.NotSendable, ScheduledMessageErrors.NotSendableMessageKey);

        var bookingId = row.BookingId;
        db.ChangeTracker.Clear();
        var result = await dispatcher.TryDispatchAsync(id, Now(), ScheduledMessageDispatchMode.SendNow, userId, ct);
        switch (result.Outcome)
        {
            case ScheduledMessageDispatchOutcome.Blocked:
                throw new DomainRuleException(ScheduledMessageErrors.Blocked, ScheduledMessageErrors.BlockedMessageKey)
                {
                    Extensions = new Dictionary<string, object?> { ["missing"] = new[] { result.Code } },
                };
            case ScheduledMessageDispatchOutcome.InProgress:
                throw new DomainConflictException(ScheduledMessageErrors.InProgress, ScheduledMessageErrors.InProgressMessageKey);
            case ScheduledMessageDispatchOutcome.NothingToDo:
                throw new DomainConflictException(ScheduledMessageErrors.NotSendable, ScheduledMessageErrors.NotSendableMessageKey);
        }

        db.ChangeTracker.Clear();
        return (await ViewsAsync(scope, bookingId, id, ct)).Single();
    }

    public async Task<RoutineActionResult> OnRoutineActionAsync(Guid routineId, int actionIndex, Guid bookingId, CancellationToken ct = default)
    {
        const RoutineActionType type = RoutineActionType.MessageToGuest;
        if (!Enabled)
            return RoutineActionResult.Failed(actionIndex, type, RoutineCodes.ActionNotAvailable);

        var now = Now();
        var found = await db.Routines
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(r => r.Id == routineId)
            .Select(r => new { r.OrgId, Planning = new PlanningRoutine(r.Id, r.Name, r.EventType, r.EventConfig, r.ActionsJson, r.Active, r.TestMode) })
            .FirstOrDefaultAsync(ct);
        if (found is null
            || !RoutineAction.TryParseList(found.Planning.ActionsJson, out var actions)
            || actionIndex < 0
            || actionIndex >= actions.Count
            || actions[actionIndex].Type != type
            || !RoutineActionRules.HasRequiredFields(actions[actionIndex]))
        {
            return RoutineActionResult.Failed(actionIndex, type, RoutineCodes.ActionsInvalid);
        }

        var orgId = found.OrgId;
        var routine = found.Planning;
        var stay = await LoadStayAsync(bookingId, ct);
        if (stay is null || stay.OrgId != orgId)
            return RoutineActionResult.Blocked(actionIndex, type, "booking_not_found");

        var row = await db.ScheduledMessages
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.BookingId == bookingId && m.OrgId == orgId && m.RoutineId == routineId && m.ActionIndex == actionIndex, ct);
        if (row is null)
        {
            // The routine fires for a message the plan has not written: an event it could not foresee (a payment, a message), or a booking the plan has not reached yet. The row is written now, for the moment
            // the plan gives it (the send window waits) or, with none, for now inside the window.
            var (from, to) = options.Value.EffectiveSendWindow;
            var planned = ScheduledMessagePlanner.Plan(routine, stay.Planning, options.Value).FirstOrDefault(p => p.ActionIndex == actionIndex)?.PlannedAtUtc
                          ?? ScheduledMessagePlanner.IntoWindow(now, RomeCalendar.ZoneOf(stay.Planning.Timezone), from, to);
            var fresh = NewRow(stay, routine, new PlannedMessage(actionIndex, actions[actionIndex].GetString("templateId")!.Trim(), planned), now);
            db.ScheduledMessages.Add(fresh);
            try
            {
                await db.SaveChangesAsync(ct);
                row = fresh;
            }
            catch (DbUpdateException ex) when (IsPlanConflict(ex))
            {
                // The plan wrote it at the same moment: that row is the one.
                db.Entry(fresh).State = EntityState.Detached;
                row = await db.ScheduledMessages
                    .IgnoreQueryFilters([Tenant])
                    .AsNoTracking()
                    .FirstAsync(m => m.BookingId == bookingId && m.OrgId == orgId && m.RoutineId == routineId && m.ActionIndex == actionIndex, ct);
            }
        }

        switch (row.Status)
        {
            case ScheduledMessageStatus.Sent:
                return RoutineActionResult.Done(actionIndex, type);
            case ScheduledMessageStatus.Skipped or ScheduledMessageStatus.Cancelled or ScheduledMessageStatus.Expired:
                return RoutineActionResult.Skipped(actionIndex, type, row.SkipReason ?? "scheduled_message_ended");
        }

        db.ChangeTracker.Clear();
        var result = await dispatcher.TryDispatchAsync(row.Id, now, ScheduledMessageDispatchMode.Routine, null, ct);
        return result.Outcome switch
        {
            ScheduledMessageDispatchOutcome.Blocked => RoutineActionResult.Blocked(actionIndex, type, result.Code ?? "scheduled_message_blocked"),
            ScheduledMessageDispatchOutcome.Failed => RoutineActionResult.Failed(actionIndex, type, result.Code ?? DispatcherUnexpected),
            ScheduledMessageDispatchOutcome.Skipped or ScheduledMessageDispatchOutcome.Cancelled => RoutineActionResult.Skipped(actionIndex, type, result.Code ?? "scheduled_message_ended"),

            // Sent, waiting for its time in the send window, already handled by another actor: the message is in hand.
            _ => RoutineActionResult.Done(actionIndex, type),
        };
    }

    private const string DispatcherUnexpected = ScheduledMessageDispatcher.UnexpectedErrorCode;

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The rows
    // ------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>What the plan reads of a booking: its org and property, the facts the planner needs, and the two more the condition of a routine can read (where it comes from, how many guests).</summary>
    private sealed record Stay(Guid OrgId, Guid PropertyId, BookingSource Source, int NumberOfGuests, PlanningBooking Planning);

    /// <summary>A routine as the plan reads it: what the planner needs, and its condition (read by the evaluator of the engine).</summary>
    private sealed record RoutineRow(PlanningRoutine Planning, string ConditionJson);

    private Task<Stay?> LoadStayAsync(Guid bookingId, CancellationToken ct) =>
        db.Bookings
            .IgnoreQueryFilters([Tenant])
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new Stay(
                b.OrgId,
                b.PropertyId,
                b.Source,
                b.NumberOfGuests,
                new PlanningBooking(
                    b.Id,
                    b.Status,
                    b.NoShowAt != null,
                    b.CheckInDate,
                    b.CheckOutDate,
                    b.CreatedAt,
                    b.ConfirmedAt,
                    b.Property.CheckInFrom,
                    b.Property.CheckOutBy,
                    b.Property.Timezone)))
            .FirstOrDefaultAsync(ct);

    private static ScheduledMessage NewRow(Stay stay, PlanningRoutine routine, PlannedMessage planned, DateTime now) => new()
    {
        OrgId = stay.OrgId,
        PropertyId = stay.PropertyId,
        BookingId = stay.Planning.Id,
        RoutineId = routine.Id,
        RoutineName = Cut(routine.Name, ScheduledMessage.RoutineNameMaxLength),
        ActionIndex = planned.ActionIndex,
        TemplateRef = Cut(planned.TemplateRef, ScheduledMessage.TemplateRefMaxLength),
        PlannedAtUtc = planned.PlannedAtUtc,
        Status = ScheduledMessageStatus.Scheduled,
        CreatedAt = now,
        UpdatedAt = now,
    };

    /// <summary>
    /// A row the plan still holds: a sent, skipped or expired one is never overwritten (an edit is never touched either); a planned, blocked or failed one moves with the booking and follows its routine; one that was
    /// cancelled because the routine was off, the stay did not happen or the stay did not meet the condition comes back when the routine holds it again and its moment has not passed. Returns 1 when the row changed.
    /// </summary>
    private static int Follow(ScheduledMessage row, PlanningRoutine routine, PlannedMessage planned, DateTime gone, DateTime now)
    {
        if (row.Status == ScheduledMessageStatus.Cancelled
            && row.SkipReason is ScheduledMessageReasons.RoutineInactive or ScheduledMessageReasons.BookingCancelled or ScheduledMessageReasons.ConditionNotMet
            && planned.PlannedAtUtc >= gone)
        {
            row.Status = ScheduledMessageStatus.Scheduled;
            row.SkipReason = null;
            row.SkippedAt = null;
            row.BlockedReason = null;
            row.PlannedAtUtc = planned.PlannedAtUtc;
            row.RoutineName = Cut(routine.Name, ScheduledMessage.RoutineNameMaxLength);
            row.TemplateRef = Cut(planned.TemplateRef, ScheduledMessage.TemplateRefMaxLength);
            row.UpdatedAt = now;
            return 1;
        }

        if (!row.IsOpen)
            return 0;

        var templateRef = Cut(planned.TemplateRef, ScheduledMessage.TemplateRefMaxLength);
        var routineName = Cut(routine.Name, ScheduledMessage.RoutineNameMaxLength);
        var moved = row.PlannedAtUtc != planned.PlannedAtUtc;
        if (!moved && row.TemplateRef == templateRef && row.RoutineName == routineName)
            return 0;

        row.TemplateRef = templateRef;
        row.RoutineName = routineName;
        row.UpdatedAt = now;
        if (moved)
        {
            // A planned message that moves into the past missed its moment; one that is blocked or failed was already due and keeps waiting for the host or the sweep.
            if (row.Status == ScheduledMessageStatus.Scheduled && planned.PlannedAtUtc < gone)
            {
                row.Status = ScheduledMessageStatus.Expired;
                row.SkipReason = ScheduledMessageReasons.Missed;
                row.LastCheckedAt = null;
            }
            else
            {
                row.PlannedAtUtc = planned.PlannedAtUtc;
            }
        }

        return 1;
    }

    private static void Close(ScheduledMessage row, ScheduledMessageStatus status, string reason, DateTime now)
    {
        row.Status = status;
        row.SkipReason = reason;
        row.ActivityId = null;
        row.LastCheckedAt = null;
        row.UpdatedAt = now;
    }

    /// <summary>The row of the scope, tracked for a change: the org of the scope and the properties it reaches are in the SQL, so a row of another org or out of the reach is the 404 of one that does not exist.</summary>
    private async Task<ScheduledMessage> OwnRowAsync(HostScope scope, Guid id, CancellationToken ct) =>
        await db.ScheduledMessages
            .Where(m => m.OrgId == scope.OrgId && m.Id == id)
            .InScope(scope, m => m.Property)
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("The scheduled message was not found or is not within reach")
        {
            Code = ScheduledMessageErrors.NotFound,
            MessageKey = ScheduledMessageErrors.NotFoundMessageKey,
        };

    private async Task SaveOwnAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Somebody else changed the message (or a send claimed it) while the caller was changing it: the one that lost reads it again.
            db.ChangeTracker.Clear();
            throw new DomainConflictException(ScheduledMessageErrors.Changed, ScheduledMessageErrors.ChangedMessageKey);
        }
    }

    private async Task CloseTodoIfIdleAsync(Guid orgId, Guid bookingId, Guid exceptRowId, CancellationToken ct)
    {
        try
        {
            await ScheduledMessageTodos.CloseIfIdleAsync(db, activities, orgId, bookingId, exceptRowId, logger, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The To do of the scheduled messages of booking {BookingId} (org {OrgId}) could not be closed", bookingId, orgId);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------
    // The views
    // ------------------------------------------------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<ScheduledMessageView>> ViewsAsync(HostScope scope, Guid bookingId, Guid? onlyId, CancellationToken ct)
    {
        var timezone = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == scope.OrgId && b.Id == bookingId)
            .InScope(scope)
            .Select(b => b.Property.Timezone)
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("The booking was not found or is not within reach") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

        var rows = db.ScheduledMessages
            .AsNoTracking()
            .Where(m => m.OrgId == scope.OrgId && m.BookingId == bookingId)
            .InScope(scope, m => m.Property);
        if (onlyId is { } id)
            rows = rows.Where(m => m.Id == id);

        var list = await rows.OrderBy(m => m.PlannedAtUtc).ThenBy(m => m.ActionIndex).ThenBy(m => m.Id).ToListAsync(ct);
        if (list.Count == 0)
            return [];

        // How the guest is reached now, read once for the booking and without side effects; a channel manager that cannot be asked is no way to the guest for the moment.
        var channel = new MessageContactOptions(MessageContactChannel.None, null, null, false, false);
        if (list.Any(m => m.IsOpen))
        {
            try
            {
                channel = await replies.ChooseChannelAsync(scope, bookingId, ct);
            }
            catch (Exception ex) when (ex is OctorateException or DomainException or NotFoundException)
            {
                logger.LogInformation("The channel of booking {BookingId} (org {OrgId}) could not be read for the timeline", bookingId, scope.OrgId);
                channel = new MessageContactOptions(MessageContactChannel.None, null, "channel_unknown", false, false);
            }
        }

        var zone = string.IsNullOrWhiteSpace(timezone) ? DefaultZone : timezone;
        var views = new List<ScheduledMessageView>(list.Count);
        foreach (var row in list)
        {
            var preview = row.IsOpen ? await dispatcher.PreviewAsync(row.Id, ct) : new ScheduledMessagePreview(null, []);
            views.Add(new ScheduledMessageView(
                row.Id,
                row.BookingId,
                row.RoutineId,
                row.RoutineName,
                row.ActionIndex,
                DateTime.SpecifyKind(row.PlannedAtUtc, DateTimeKind.Utc),
                zone,
                row.Status,
                row.BlockedReason,
                row.SkipReason,
                !string.IsNullOrEmpty(row.TextOverride),
                row.OutsideHours,
                row.SentAt,
                row.SentThreadKey,
                row.IsOpen ? channel.Channel : MessageContactChannel.None,
                row.IsOpen ? channel.Reason : null,
                preview.Text,
                preview.MissingVariables,
                row.IsOpen,
                row.IsOpen,
                row.IsOpen));
        }

        return views;
    }

    // ------------------------------------------------------------------------------------------------------------------------------------

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsPlanConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ScheduledMessage.PlanIndexName };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
