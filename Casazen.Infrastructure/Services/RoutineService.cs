using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Jobs;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The routine engine (NF-21, NF-26, ADR-022): runs the active routines of an org for an event of the bus, with real conditions, guardrails that read
/// the booking, scheduled waits and a structured result (E01/F04/US01/T01).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Condition</b>: <c>ConditionJson</c> is read as a condition of ADR-014 and evaluated by <see cref="IConditionEvaluator"/> on the facts of the
/// booking the payload names, org-wide as the jobs do. Not met: nothing is recorded. Invalid: an execution <c>Failed</c> with
/// <see cref="RoutineCodes.ConditionInvalid"/>, which the host sees in the log.</item>
/// <item><b>Guardrails</b> (ADR-022-R11) run before every action, also in test mode, on facts read from the booking and never from the payload: what
/// cannot be read is not a pass.</item>
/// <item><b>Actions</b> run in order and stop at the first one that fails; a blocked action does not stop the next ones. In test mode none runs: each is
/// <c>simulated</c>. The switch of <see cref="ExecuteActionAsync"/> has one case for each action, owned by the task that makes it real; what has no case
/// fails with <see cref="RoutineCodes.ActionNotAvailable"/>, it never pretends.</item>
/// <item><b>Wait</b> saves the payload and the next action in <c>ResultJson</c>, sets <c>ResumeAt</c> and schedules
/// <see cref="RoutineDelayJob.ResumeRoutineAsync"/> at that time; <see cref="ResumeAsync"/> continues from what was saved, once.</item>
/// <item><b>Failures</b>: an expected one is the <c>failed</c> result of an action; an unexpected one closes the execution with
/// <see cref="RoutineCodes.ActionError"/> and is rethrown, so the dispatcher records it on the event (ADR-022-R5).</item>
/// <item><b>The flag</b> <c>Features:Routines</c> closes the module (E01/F04/US01/T03): the events start nothing, and a wait that comes due ends its
/// execution <c>Failed</c> with <see cref="RoutineCodes.Inactive"/>.</item>
/// <item>The org is the one of the routine in every query (the tenant filter of the scope is a second wall); the job that resumes a wait has no tenant
/// and relies on the first.</item>
/// </list>
/// </remarks>
public class RoutineService(
    AppDbContext db,
    IConditionEvaluator conditionEvaluator,
    IInternalActivityService activities,
    IBackgroundJobClient jobs,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<RoutineService> logger) : IRoutineService
{
    /// <summary>
    /// The unique index on <c>RoutineExecutions (RoutineId, EventId)</c> (migration <c>AddRoutineEngine</c>): a second execution of
    /// a routine for the same event is refused by the database (ADR-022-R7).
    /// </summary>
    internal const string ExecutionPerEventIndexName = "IX_RoutineExecutions_RoutineId_EventId";

    /// <summary>The code of a <see cref="NotFoundException"/> that names none: the generic one of the API.</summary>
    private const string GenericNotFoundCode = "not_found";

    public Task TriggerAsync(
        Guid orgId,
        RoutineEventType eventType,
        string eventId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        return TriggerAsync(orgId, eventType, eventId, payload, static _ => true, cancellationToken);
    }

    public async Task TriggerAsync(
        Guid orgId,
        RoutineEventType eventType,
        string eventId,
        object payload,
        Func<Routine, bool> routineFilter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routineFilter);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

        // The module is closed (Features:Routines): the events of the bus start nothing.
        if (!featureFlags.IsEnabled(FeatureFlags.Routines))
        {
            logger.LogDebug("Routines are off: event {EventId} of org {OrgId} starts no routine", eventId, orgId);
            return;
        }

        var data = RoutinePayload.Normalize(payload);

        // Find all active routines for this event type and org (TN-1: tenant isolation), then the ones the caller asks for
        var routines = (await db.Routines
                .Where(r => r.OrgId == orgId && r.Active && r.EventType == eventType)
                .ToListAsync(cancellationToken))
            .Where(routineFilter)
            .ToList();

        logger.LogInformation(
            "Triggering {Count} routine(s) for org {OrgId} event {EventType} with ID {EventId}",
            routines.Count, orgId, eventType, eventId);

        foreach (var routine in routines)
        {
            await ExecuteRoutineAsync(routine, eventId, data, cancellationToken);
        }
    }

    public async Task ResumeAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        // The job has no tenant: the execution is found by its id, and everything after it reads in the org of the execution.
        var execution = await db.RoutineExecutions
            .Include(e => e.Routine)
            .FirstOrDefaultAsync(e => e.Id == executionId, cancellationToken);

        if (execution == null)
        {
            logger.LogWarning("Routine execution {ExecutionId} not found for resume", executionId);
            return;
        }

        // A second job for the same wait, or one that comes after the execution ended: nothing to continue (ADR-022-R10).
        if (execution.Outcome != RoutineExecutionOutcomes.Waiting)
        {
            logger.LogInformation(
                "Routine execution {ExecutionId} is {Outcome}, not waiting: nothing to resume",
                executionId, execution.Outcome);
            return;
        }

        var saved = RoutineExecutionResult.Parse(execution.ResultJson);
        var results = saved.Actions.ToList();

        // The wait that brought the execution here is over.
        var waited = results.FindLastIndex(r => r.Outcome == RoutineActionOutcomes.Waiting);
        if (waited >= 0)
            results[waited] = results[waited] with { Outcome = RoutineActionOutcomes.Done };

        // A routine deactivated meanwhile, or the module closed (Features:Routines), does not continue: the execution ends and says so.
        if (!featureFlags.IsEnabled(FeatureFlags.Routines) || execution.Routine is not { Active: true } routine)
        {
            logger.LogInformation(
                "Routine {RoutineId} is not active any more (or routines are off): execution {ExecutionId} ends instead of continuing",
                execution.RoutineId, executionId);
            await EndAsync(
                execution,
                RoutineExecutionOutcomes.Failed,
                new RoutineExecutionResult(results, null, saved.NextActionIndex, RoutineCodes.Inactive),
                cancellationToken);
            return;
        }

        logger.LogInformation(
            "Resuming routine execution {ExecutionId} for routine {RoutineId} at action {NextActionIndex}",
            executionId, execution.RoutineId, saved.NextActionIndex);

        var run = new Run(db, execution.OrgId, RoutinePayload.FromJson(saved.PayloadJson));
        await RunAsync(routine, execution, run, results, saved.NextActionIndex, cancellationToken);
    }

    public async Task<RoutineSimulationResult> SimulateAsync(
        Guid orgId,
        Guid routineId,
        object sampleEventPayload,
        CancellationToken cancellationToken = default)
    {
        var routine = await db.Routines
                          .AsNoTracking()
                          .FirstOrDefaultAsync(r => r.Id == routineId && r.OrgId == orgId, cancellationToken)
                      ?? throw new NotFoundException($"Routine {routineId} not found")
                      {
                          Code = RoutineCodes.NotFound,
                          MessageKey = "RoutineNotFound",
                      };

        var run = new Run(db, orgId, RoutinePayload.Normalize(sampleEventPayload));

        // The same evaluation as a real event: a condition the engine would refuse is reported, not guessed at.
        var verdict = await EvaluateConditionAsync(routine, run, cancellationToken);
        var actionsValid = RoutineAction.TryParseList(routine.ActionsJson, out var actions);

        var simulated = new List<RoutineSimulatedAction>();
        for (var index = 0; index < actions.Count; index++)
        {
            var action = actions[index];

            // What stops it for real: a guardrail, or no implementation (the routine in test mode may hold any action; live it would fail).
            var reason = await CheckGuardrailsAsync(run, action, cancellationToken)
                         ?? (RoutineActionCatalog.IsAvailable(action.Type, featureFlags) ? null : RoutineCodes.ActionNotAvailable);
            simulated.Add(new RoutineSimulatedAction(index, action.Type, action.ConfigElement, reason is not null, reason));
        }

        var matched = verdict == ConditionVerdict.Met;
        var code = verdict == ConditionVerdict.Invalid
            ? RoutineCodes.ConditionInvalid
            : actionsValid ? null : RoutineCodes.ActionsInvalid;
        return new RoutineSimulationResult(routine.Id, routine.Name, matched, matched && actionsValid, simulated, code);
    }

    private async Task ExecuteRoutineAsync(
        Routine routine,
        string eventId,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        // Check for idempotence: has this routine already executed for this event?
        var alreadyExecuted = await db.RoutineExecutions
            .AnyAsync(
                e => e.OrgId == routine.OrgId && e.RoutineId == routine.Id && e.EventId == eventId,
                cancellationToken);

        if (alreadyExecuted)
        {
            logger.LogInformation(
                "Routine {RoutineId} already executed for event {EventId}, skipping (idempotence)",
                routine.Id, eventId);
            return;
        }

        var run = new Run(db, routine.OrgId, payload);
        var verdict = await EvaluateConditionAsync(routine, run, cancellationToken);
        if (verdict == ConditionVerdict.NotMet)
        {
            logger.LogInformation(
                "Routine {RoutineId} conditions not met for event {EventId}",
                routine.Id, eventId);
            return;
        }

        // Create execution record
        var execution = new RoutineExecution
        {
            RoutineId = routine.Id,
            OrgId = routine.OrgId,
            EventId = eventId,
            StartedAt = Now(),
            Outcome = RoutineExecutionOutcomes.Running,
        };

        db.RoutineExecutions.Add(execution);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsDuplicateExecution(ex))
        {
            // A parallel delivery of the same event claimed this routine first: the unique index refused this second claim
            // (ADR-022-R7). The routine has run once; nothing failed.
            db.Entry(execution).State = EntityState.Detached;
            logger.LogInformation(
                "Routine {RoutineId} already executed for event {EventId} by a parallel delivery, skipping (idempotence)",
                routine.Id, eventId);
            return;
        }

        if (verdict == ConditionVerdict.Invalid)
        {
            logger.LogWarning(
                "Routine {RoutineId} has a condition that cannot be evaluated: execution {ExecutionId} failed",
                routine.Id, execution.Id);
            await EndAsync(
                execution,
                RoutineExecutionOutcomes.Failed,
                new RoutineExecutionResult([], null, 0, RoutineCodes.ConditionInvalid),
                cancellationToken);
            return;
        }

        await RunAsync(routine, execution, run, [], 0, cancellationToken);
    }

    /// <summary>
    /// Runs the actions of <paramref name="routine"/> from <paramref name="startIndex"/> and closes the execution: <c>Success</c>, <c>Failed</c>, or
    /// <c>Waiting</c> at a Wait. <paramref name="results"/> holds what the actions before it did (a resumed execution).
    /// </summary>
    private async Task RunAsync(
        Routine routine,
        RoutineExecution execution,
        Run run,
        List<RoutineActionResult> results,
        int startIndex,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!RoutineAction.TryParseList(routine.ActionsJson, out var actions))
            {
                logger.LogWarning("Routine {RoutineId}: its actions cannot be read, execution {ExecutionId} failed", routine.Id, execution.Id);
                await EndAsync(
                    execution,
                    RoutineExecutionOutcomes.Failed,
                    new RoutineExecutionResult(results, null, startIndex, RoutineCodes.ActionsInvalid),
                    cancellationToken);
                return;
            }

            var next = startIndex;
            for (var index = startIndex; index < actions.Count; index++)
            {
                var action = actions[index];
                next = index + 1;

                // Apply guardrails (AC-NF26-4, AC-NF26-5, AC-NF26-6, ADR-022-R11): a blocked action is not run, and the next ones go on
                var guardrail = await CheckGuardrailsAsync(run, action, cancellationToken);
                if (guardrail is not null)
                {
                    logger.LogWarning(
                        "Routine {RoutineId} action {ActionType} blocked by guardrail: {Reason}",
                        routine.Id, action.Type, guardrail);
                    results.Add(RoutineActionResult.Blocked(index, action.Type, guardrail));
                    continue;
                }

                // Test mode (AC-NF26-7): no case runs, no wait is scheduled
                if (routine.TestMode)
                {
                    logger.LogInformation(
                        "Routine {RoutineId} test mode: would execute {ActionType}",
                        routine.Id, action.Type);
                    results.Add(RoutineActionResult.Simulated(index, action.Type));
                    continue;
                }

                if (action.Type == RoutineActionType.Wait)
                {
                    // Handle Wait action (AC-NF26-10, ADR-022-R10): the execution is suspended until the job resumes it
                    await WaitAsync(execution, run, results, index, action, cancellationToken);
                    return;
                }

                var result = await ExecuteActionAsync(routine, execution, index, action, run.Payload, cancellationToken);
                results.Add(result);
                if (result.Outcome == RoutineActionOutcomes.Failed)
                    break;
            }

            var failed = results.Any(r => r.Outcome == RoutineActionOutcomes.Failed);
            await EndAsync(
                execution,
                failed ? RoutineExecutionOutcomes.Failed : RoutineExecutionOutcomes.Success,
                new RoutineExecutionResult(results, null, next),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Routine {RoutineId} execution {ExecutionId} failed on an unexpected error ({Code})",
                routine.Id, execution.Id, run.FailureCode);
            await RecordFailureAsync(execution.Id, run.FailureCode);
            throw;
        }
    }

    /// <summary>
    /// A Wait: the payload and the next action are saved with the execution, which becomes <c>Waiting</c> until <c>ResumeAt</c>, and the job that resumes it
    /// is scheduled. A wait that is out of range (the API refuses it, a routine saved some other way may not) fails the action.
    /// </summary>
    private async Task WaitAsync(
        RoutineExecution execution,
        Run run,
        List<RoutineActionResult> results,
        int index,
        RoutineAction action,
        CancellationToken cancellationToken)
    {
        if (RoutineActionRules.WaitDelay(action) is not { } delay)
        {
            results.Add(RoutineActionResult.Failed(index, action.Type, RoutineCodes.ActionsInvalid));
            await EndAsync(
                execution,
                RoutineExecutionOutcomes.Failed,
                new RoutineExecutionResult(results, null, index + 1),
                cancellationToken);
            return;
        }

        var resumeAt = timeProvider.GetUtcNow().Add(delay);
        results.Add(RoutineActionResult.Waiting(index, action.Type));
        execution.Outcome = RoutineExecutionOutcomes.Waiting;
        execution.ResumeAt = resumeAt.UtcDateTime;
        execution.CompletedAt = null;
        execution.ResultJson = new RoutineExecutionResult(results, RoutinePayload.ToJson(run.Payload), index + 1).ToJson();
        await db.SaveChangesAsync(cancellationToken);

        // Scheduling is the one call outside the database: when it fails the wait can never end, so the execution says so and the failure goes on.
        var executionId = execution.Id;
        run.FailureCode = RoutineCodes.WaitNotScheduled;
        jobs.Schedule<RoutineDelayJob>(job => job.ResumeRoutineAsync(executionId, CancellationToken.None), resumeAt);
        run.FailureCode = RoutineCodes.ActionError;

        logger.LogInformation(
            "Routine {RoutineId} waiting until {ResumeAt}: execution {ExecutionId}",
            execution.RoutineId, resumeAt, executionId);
    }

    /// <summary>
    /// The one place that runs an action: a case for each action, owned by the task that makes it real (<c>CreateActivity</c>: E01/F04/US01/T02).
    /// What the catalog does not list as available, and what has no case yet, fails with <see cref="RoutineCodes.ActionNotAvailable"/>: nothing here
    /// pretends to have done what it did not.
    /// </summary>
    private async Task<RoutineActionResult> ExecuteActionAsync(
        Routine routine,
        RoutineExecution execution,
        int actionIndex,
        RoutineAction action,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        // The catalog is the one source of what can run: an action it does not list as available never reaches its case.
        if (RoutineActionCatalog.IsAvailable(action.Type, featureFlags))
        {
            switch (action.Type)
            {
                case RoutineActionType.CreateActivity:
                    return await CreateActivityAsync(routine, actionIndex, action, payload, cancellationToken);
            }
        }

        logger.LogWarning(
            "Routine {RoutineId} execution {ExecutionId}: action {ActionType} is not available",
            routine.Id, execution.Id, action.Type);
        return RoutineActionResult.Failed(actionIndex, action.Type, RoutineCodes.ActionNotAvailable);
    }

    /// <summary>
    /// <c>CreateActivity</c> (E01/F04/US01/T02): a To do for the booking of the event, in the org of the routine, with the words the host wrote as its
    /// reason (<c>text</c>, never guest data) and a deadline of <c>dueInDays</c> days from now when it says so. The booking is read in the org of the
    /// routine, and its property is the one of the To do. The activity service raises one open To do for each type, entity type and entity id, so a
    /// redelivered event, a resume or another routine asking for the same booking creates no second one. A booking the event does not name, or the org
    /// does not have, fails the action; so does a property that is gone (the code the activity service answers).
    /// </summary>
    private async Task<RoutineActionResult> CreateActivityAsync(
        Routine routine,
        int actionIndex,
        RoutineAction action,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        if (!RoutinePayload.TryGetId(payload, "bookingId", out var bookingId))
            return RoutineActionResult.Failed(actionIndex, action.Type, RoutineCodes.ActivityNeedsBooking);

        var booking = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId && b.OrgId == routine.OrgId)
            .Select(b => new { b.Id, b.PropertyId })
            .FirstOrDefaultAsync(cancellationToken);
        if (booking is null)
            return RoutineActionResult.Failed(actionIndex, action.Type, RoutineCodes.ActivityNeedsBooking);

        if (!RoutineActionRules.TryReadActivity(action, out var text, out var dueInDays))
            return RoutineActionResult.Failed(actionIndex, action.Type, RoutineCodes.ActionsInvalid);

        var request = new CreateTodoRequest
        {
            Type = ActivityType.RoutineTask,
            Action = ActivityAction.ReviewBooking,
            EntityType = InternalActivity.BookingEntityType,
            EntityId = booking.Id,
            Urgency = ActivityUrgency.Medium,
            PropertyId = booking.PropertyId,
            OrgId = routine.OrgId,
            Deadline = dueInDays is { } days ? Now().AddDays(days) : null,
            Reason = text,
        };

        try
        {
            await activities.CreateAsync(request, cancellationToken);
        }
        catch (NotFoundException ex)
        {
            // The property of the booking is gone (deleted since): there is nothing to raise the To do on, and a retry would find the same.
            logger.LogWarning(
                "Routine {RoutineId}: no To do for booking {BookingId} of org {OrgId}: {Code}",
                routine.Id, booking.Id, routine.OrgId, ex.Code ?? GenericNotFoundCode);
            return RoutineActionResult.Failed(actionIndex, action.Type, ex.Code ?? GenericNotFoundCode);
        }

        return RoutineActionResult.Done(actionIndex, action.Type);
    }

    /// <summary>
    /// The guardrails of ADR-022-R11 (AC-NF26-4, AC-NF26-5, AC-NF26-6): the code of the one that stops <paramref name="action"/>, or <c>null</c>. They
    /// read the booking of the event, never its payload, and they fail closed: with no booking (or one of another org) the guarded actions are blocked.
    /// </summary>
    private static async Task<string?> CheckGuardrailsAsync(Run run, RoutineAction action, CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case RoutineActionType.SendAccessCode:
                // Access code only after the identity of the guest was verified: the latest verification of the booking is an Accept.
                return await run.IdentityVerifiedAsync(cancellationToken) ? null : RoutineCodes.AccessCodeNeedsIdVerification;

            case RoutineActionType.MessageToGuest:
                // No Airbnb contact before the booking is confirmed.
                var booking = await run.BookingAsync(cancellationToken);
                if (booking is null || (booking.Source == BookingSource.Airbnb && !IsConfirmed(booking.Status)))
                    return RoutineCodes.AirbnbNotConfirmed;

                // Campaigns only with the marketing consent the campaigns themselves read (Guest.MarketingConsent).
                if (action.GetFlag("isCampaign") && !await run.MarketingConsentAsync(cancellationToken))
                    return RoutineCodes.CampaignNeedsConsent;

                return null;

            default:
                return null;
        }
    }

    private static bool IsConfirmed(BookingStatus status) =>
        status is BookingStatus.Confirmed or BookingStatus.CheckedIn or BookingStatus.CheckedOut;

    /// <summary>
    /// Reads the condition of <paramref name="routine"/> and evaluates it on the facts of the booking of the event. No condition is met; a condition
    /// the evaluator or the routines refuse is invalid; a rule on a fact that is not known is not met.
    /// </summary>
    private async Task<ConditionVerdict> EvaluateConditionAsync(Routine routine, Run run, CancellationToken cancellationToken)
    {
        var parsed = RoutineConditions.Parse(routine.ConditionJson);
        if (!parsed.Valid)
            return ConditionVerdict.Invalid;

        if (parsed.Condition is not { } condition)
            return ConditionVerdict.Met;

        var errors = conditionEvaluator.ValidateCondition(condition)
            .Concat(RoutineConditions.UnsupportedFieldErrors(condition))
            .ToList();
        if (errors.Count > 0)
        {
            logger.LogWarning(
                "Routine {RoutineId} of org {OrgId} has an invalid condition: {Errors}",
                routine.Id, routine.OrgId, string.Join("; ", errors));
            return ConditionVerdict.Invalid;
        }

        // No rule at all (the {} of a routine with no condition): nothing to prove, nothing to read.
        if (condition.Groups.All(group => group.Rules.Count == 0))
            return ConditionVerdict.Met;

        var facts = await run.FactsAsync(cancellationToken);
        var met = await conditionEvaluator.EvaluateAsync(condition, facts, new HostScope(routine.OrgId), cancellationToken);
        return met ? ConditionVerdict.Met : ConditionVerdict.NotMet;
    }

    private async Task EndAsync(
        RoutineExecution execution,
        string outcome,
        RoutineExecutionResult result,
        CancellationToken cancellationToken)
    {
        execution.Outcome = outcome;
        execution.CompletedAt = Now();
        execution.ResultJson = result.ToJson();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Closes an execution as <c>Failed</c> with <paramref name="code"/> after an unexpected error (the code is all the host needs: no message of the
    /// exception is kept). The unit of work may hold the change that failed, so it starts again from what the database says; if even this cannot be
    /// saved, it is logged and the original error goes on.
    /// </summary>
    private async Task RecordFailureAsync(Guid executionId, string code)
    {
        try
        {
            db.ChangeTracker.Clear();
            var execution = await db.RoutineExecutions.FirstOrDefaultAsync(e => e.Id == executionId, CancellationToken.None);
            if (execution is null)
                return;

            execution.Outcome = RoutineExecutionOutcomes.Failed;
            execution.CompletedAt = Now();
            execution.ResultJson = (RoutineExecutionResult.Parse(execution.ResultJson) with { PayloadJson = null, Code = code }).ToJson();
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception recordingError)
        {
            logger.LogError(recordingError, "Routine execution {ExecutionId} could not be marked as failed", executionId);
        }
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private static bool IsDuplicateExecution(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: ExecutionPerEventIndexName };

    private enum ConditionVerdict
    {
        Met,
        NotMet,
        Invalid,
    }

    /// <summary>The columns of the booking an event is about that the engine reads, never the guest or the prices.</summary>
    private sealed record BookingFacts(
        Guid Id,
        Guid PropertyId,
        Guid GuestId,
        BookingSource Source,
        BookingStatus Status,
        DateTime CheckInDate,
        DateTime CheckOutDate,
        int NumberOfGuests,
        DateTime CreatedAt);

    /// <summary>
    /// One run of a routine: the payload of the event, and the facts of its booking read once, on first use, in the org of the routine. A resumed
    /// execution gets a new one, so it reads the booking as it is when the wait ends.
    /// </summary>
    private sealed class Run(AppDbContext db, Guid orgId, IReadOnlyDictionary<string, object?> payload)
    {
        private bool _bookingRead;
        private BookingFacts? _booking;
        private bool? _identityVerified;
        private bool? _marketingConsent;

        public IReadOnlyDictionary<string, object?> Payload { get; } = payload;

        /// <summary>The code an unexpected error is recorded with: <see cref="RoutineCodes.ActionError"/>, or the one of the call that was being made.</summary>
        public string FailureCode { get; set; } = RoutineCodes.ActionError;

        /// <summary>The booking the payload names (<c>bookingId</c>), in the org of the routine; <c>null</c> when it names none or the org has no such booking.</summary>
        public async Task<BookingFacts?> BookingAsync(CancellationToken cancellationToken)
        {
            if (_bookingRead)
                return _booking;

            _bookingRead = true;
            if (!RoutinePayload.TryGetId(Payload, "bookingId", out var bookingId))
                return null;

            _booking = await db.Bookings
                .AsNoTracking()
                .Where(b => b.Id == bookingId && b.OrgId == orgId)
                .Select(b => new BookingFacts(
                    b.Id,
                    b.PropertyId,
                    b.GuestId,
                    b.Source,
                    b.Status,
                    b.CheckInDate,
                    b.CheckOutDate,
                    b.NumberOfGuests,
                    b.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
            return _booking;
        }

        /// <summary>The facts a condition is evaluated on: those of the booking, none when there is none.</summary>
        public async Task<RoutineConditionFacts> FactsAsync(CancellationToken cancellationToken)
        {
            var booking = await BookingAsync(cancellationToken);
            return booking is null
                ? RoutineConditionFacts.None
                : RoutineConditionFacts.OfBooking(
                    booking.PropertyId,
                    booking.Source,
                    booking.CheckInDate,
                    booking.CheckOutDate,
                    booking.NumberOfGuests,
                    booking.CreatedAt);
        }

        /// <summary>
        /// The verification gate of the access code (AC-NF13-6, AC-NF26-4): the latest <see cref="BiometricVerificationLog"/> of the booking is an
        /// <see cref="BiometricOutcome.Accept"/>, written by the agent or by an operator's approval; the same gate as <c>AccessCodeService</c>.
        /// </summary>
        public async Task<bool> IdentityVerifiedAsync(CancellationToken cancellationToken)
        {
            if (_identityVerified is { } known)
                return known;

            var booking = await BookingAsync(cancellationToken);
            if (booking is null)
                return (_identityVerified = false).Value;

            var latest = await db.BiometricVerificationLogs
                .AsNoTracking()
                .Where(v => v.OrgId == orgId && v.BookingId == booking.Id)
                .OrderByDescending(v => v.CreatedAt)
                .ThenByDescending(v => v.Id)
                .Select(v => (BiometricOutcome?)v.Outcome)
                .FirstOrDefaultAsync(cancellationToken);
            return (_identityVerified = latest == BiometricOutcome.Accept).Value;
        }

        /// <summary>
        /// The marketing consent of the guest of the booking, the flag the campaigns read (<c>Guest.MarketingConsent</c>, set once the e-mail is
        /// confirmed): a guest who was erased or anonymized has none.
        /// </summary>
        public async Task<bool> MarketingConsentAsync(CancellationToken cancellationToken)
        {
            if (_marketingConsent is { } known)
                return known;

            var booking = await BookingAsync(cancellationToken);
            if (booking is null)
                return (_marketingConsent = false).Value;

            var consented = await db.Guests
                .AsNoTracking()
                .AnyAsync(
                    g => g.Id == booking.GuestId
                         && g.OrgId == orgId
                         && g.MarketingConsent
                         && !g.IsDeleted
                         && !g.ErasureRequested
                         && g.DataAnonymizedDate == null,
                    cancellationToken);
            return (_marketingConsent = consented).Value;
        }
    }
}
