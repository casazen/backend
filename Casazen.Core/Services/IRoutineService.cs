using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;

namespace Casazen.Core.Services;

/// <summary>
/// Service for automation routine execution (NF-21, NF-26, ADR-022): the engine behind the event bus. The routines themselves are created,
/// changed and read through <c>IRoutineManagementService</c> (E01/F04/US01/T03).
/// </summary>
public interface IRoutineService
{
    /// <summary>
    /// Trigger routines for a given event (AC-NF26-1).
    /// Finds active routines matching the event type, evaluates conditions,
    /// and executes actions (with guardrails).
    /// </summary>
    /// <param name="orgId">Organization ID for tenant isolation (TN-1).</param>
    /// <param name="eventType">Event type that occurred.</param>
    /// <param name="eventId">Unique identifier for this event occurrence.</param>
    /// <param name="payload">Event payload (ids and codes, no personal data): any object, read as a dictionary whose names are matched in any case.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TriggerAsync(
        Guid orgId,
        RoutineEventType eventType,
        string eventId,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="TriggerAsync(Guid, RoutineEventType, string, object, CancellationToken)"/> for the routines that
    /// <paramref name="routineFilter"/> also accepts: a time trigger starts only the routines whose configured offset is its own
    /// (ADR-022-R8). A routine already executed for <paramref name="eventId"/> is skipped, also when a parallel delivery of the same
    /// event claimed it first (unique index on routine and event, ADR-022-R7).
    /// </summary>
    /// <remarks>
    /// The condition of a routine is evaluated on the facts of the booking the payload names (<c>bookingId</c>), read in the org of the routine: a
    /// routine whose condition is not met records nothing; one whose condition is invalid records an execution that failed with
    /// <see cref="RoutineCodes.ConditionInvalid"/>. The guardrails (ADR-022-R11) read the same booking and never the payload: no booking, no pass.
    /// </remarks>
    /// <param name="orgId">Organization ID for tenant isolation (TN-1).</param>
    /// <param name="eventType">Event type that occurred.</param>
    /// <param name="eventId">Unique identifier for this event occurrence, the same on every delivery of the event.</param>
    /// <param name="payload">Event payload (ids and codes, no personal data).</param>
    /// <param name="routineFilter">Which of the active routines of the event type run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TriggerAsync(
        Guid orgId,
        RoutineEventType eventType,
        string eventId,
        object payload,
        Func<Routine, bool> routineFilter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a routine execution after a Wait action (AC-NF26-10, ADR-022-R10).
    /// Called by the Hangfire job at the scheduled resume time. It continues from the action the execution saved, with the payload it saved, and does
    /// nothing for an execution that is not waiting (a second job for the same wait, or one that comes after the execution ended): the actions after a
    /// wait run once. A routine deactivated meanwhile ends the execution <c>Failed</c> with <see cref="RoutineCodes.Inactive"/>; with <c>Features:Routines</c> off it
    /// does nothing at all (the execution stays as it is).
    /// </summary>
    /// <param name="executionId">Execution to resume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Simulate routine execution in test mode (AC-NF26-7, ADR-022-R9).
    /// Evaluates the condition (the same way <see cref="TriggerAsync(Guid, RoutineEventType, string, object, CancellationToken)"/> does) and returns what
    /// would happen without executing actions: nothing is written, nothing is scheduled, nothing is sent.
    /// </summary>
    /// <param name="orgId">The org of the caller: a routine of another org is a <see cref="Exceptions.NotFoundException"/> (<c>routine_not_found</c>).</param>
    /// <param name="routineId">Routine to test.</param>
    /// <param name="sampleEventPayload">Sample event payload for testing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Simulation result showing conditions evaluated and actions that would execute.</returns>
    Task<RoutineSimulationResult> SimulateAsync(
        Guid orgId,
        Guid routineId,
        object sampleEventPayload,
        CancellationToken cancellationToken = default);
}
