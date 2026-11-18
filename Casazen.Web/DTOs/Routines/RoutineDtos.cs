using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Models;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Routines;

/// <summary>
/// A routine as the routines page reads it (E01/F04/US01/T03, <c>src/types/routine.types.ts</c>): the event type by name, and the configuration, the
/// condition and the actions as the JSON texts they are saved as.
/// </summary>
public sealed record RoutineDto(
    Guid Id,
    Guid OrgId,
    string Name,
    string EventType,
    string EventConfig,
    string ConditionJson,
    string ActionsJson,
    bool TestMode,
    bool Active)
{
    public static RoutineDto From(Routine routine)
    {
        ArgumentNullException.ThrowIfNull(routine);

        return new RoutineDto(
            routine.Id,
            routine.OrgId,
            routine.Name,
            routine.EventType.ToString(),
            routine.EventConfig,
            routine.ConditionJson,
            routine.ActionsJson,
            routine.TestMode,
            routine.Active);
    }
}

/// <summary>
/// An execution of a routine, a line of its log. <see cref="ResultJson"/> is <c>{ actions: [{ index, type, outcome, code? }], nextActionIndex, code? }</c>;
/// the payload an execution saves to continue after a wait is the engine's own state and is never shown.
/// </summary>
public sealed record RoutineExecutionDto(
    Guid Id,
    Guid OrgId,
    Guid RoutineId,
    string EventId,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string Outcome,
    DateTime? ResumeAt,
    string ResultJson)
{
    public static RoutineExecutionDto From(RoutineExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);

        return new RoutineExecutionDto(
            execution.Id,
            execution.OrgId,
            execution.RoutineId,
            execution.EventId,
            execution.StartedAt,
            execution.CompletedAt,
            execution.Outcome,
            execution.ResumeAt,
            RoutineExecutionResult.WithoutPayload(execution.ResultJson));
    }
}

/// <summary>An action of the catalog: its type, whether it can run today and, when it cannot, the code that says so.</summary>
public sealed record RoutineActionCatalogItemDto(string Type, bool Available, string? Code)
{
    public static RoutineActionCatalogItemDto From(RoutineActionAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        return new RoutineActionCatalogItemDto(availability.Type.ToString(), availability.Available, availability.Code);
    }
}

/// <summary>An action of a routine as the editor writes it: the type by name and its configuration, an object.</summary>
public sealed class RoutineActionDto
{
    public string? Type { get; init; }

    public JsonElement? Config { get; init; }
}

/// <summary>
/// The members a routine is written with (<c>name</c>, <c>eventType</c>, <c>eventConfig</c>, <c>condition</c>, <c>actions</c>, <c>testMode</c>,
/// <c>active</c>). Nothing is validated here: the service answers with a 422 and a stable code for what is not valid, so a client has one place to
/// read the reason.
/// </summary>
public abstract class RoutineWriteDto
{
    public string? Name { get; init; }

    public string? EventType { get; init; }

    public JsonElement? EventConfig { get; init; }

    public JsonElement? Condition { get; init; }

    public List<RoutineActionDto?>? Actions { get; init; }

    public bool? TestMode { get; init; }

    public bool? Active { get; init; }

    public RoutineCommand ToCommand() =>
        new(
            Name,
            EventType,
            EventConfig,
            Condition,
            Actions?.Select(action => new RoutineActionInput(action?.Type, action?.Config)).ToList(),
            TestMode,
            Active);
}

/// <summary>A new routine: a name, an event type and actions; it starts off and not in test mode unless it says so.</summary>
public sealed class CreateRoutineDto : RoutineWriteDto;

/// <summary>A change to a routine: every member optional, what is left out stays as it is.</summary>
public sealed class UpdateRoutineDto : RoutineWriteDto;

/// <summary>The body of <c>PUT api/routines/{id}/active</c>.</summary>
public sealed class SetRoutineActiveDto
{
    [Required(ErrorMessage = "RoutineActiveRequired")]
    public bool? Active { get; init; }
}

/// <summary>The body of <c>PUT api/routines/{id}/test-mode</c>.</summary>
public sealed class SetRoutineTestModeDto
{
    [Required(ErrorMessage = "RoutineTestModeRequired")]
    public bool? TestMode { get; init; }
}

public sealed record RoutineInstructionDto([Required, StringLength(500)] string Instruction);

public sealed record RoutineActionSuggestion(string? Type);
