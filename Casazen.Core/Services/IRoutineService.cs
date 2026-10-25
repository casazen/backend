using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Service for automation routine execution and management (NF-26, ADR-022).
/// </summary>
public interface IRoutineService
{
    /// <summary>
    /// Trigger routines for a given event (AC-NF26-1).
    /// Finds active routines matching the event type, evaluates conditions,
    /// and executes actions (with guardrails).
    /// </summary>
    /// <param name="eventType">Event type that occurred.</param>
    /// <param name="eventId">Unique identifier for this event occurrence.</param>
    /// <param name="payload">Event payload (entity data).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TriggerAsync(
        RoutineEventType eventType,
        string eventId,
        object payload,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a routine execution after a Wait action (AC-NF26-10, ADR-022-R10).
    /// Called by the Hangfire job at the scheduled resume time.
    /// </summary>
    /// <param name="executionId">Execution to resume.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ResumeAsync(Guid executionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Simulate routine execution in test mode (AC-NF26-7, ADR-022-R9).
    /// Evaluates conditions and returns what would happen without executing actions.
    /// </summary>
    /// <param name="routineId">Routine to test.</param>
    /// <param name="sampleEventPayload">Sample event payload for testing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Simulation result showing conditions evaluated and actions that would execute.</returns>
    Task<object> SimulateAsync(
        Guid routineId,
        object sampleEventPayload,
        CancellationToken cancellationToken = default);
}
