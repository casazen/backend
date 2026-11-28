using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Models;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Routines;

public sealed record RoutineDto(
    Guid Id,
    Guid OrgId,
    string Name,
    string EventType,
    string EventConfig,
    string ConditionJson,
    string ActionsJson,
    bool TestMode,
    bool Active,
    string? AgentObjective = null)
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
            routine.Active,
            routine.AgentObjective);
    }
}

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

public sealed record RoutineActionCatalogItemDto(string Type, bool Available, string? Code)
{
    public static RoutineActionCatalogItemDto From(RoutineActionAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(availability);

        return new RoutineActionCatalogItemDto(availability.Type.ToString(), availability.Available, availability.Code);
    }
}

public sealed class RoutineActionDto
{
    public string? Type { get; init; }

    public JsonElement? Config { get; init; }
}

public abstract class RoutineWriteDto
{
    public string? Name { get; init; }

    [StringLength(500)]
    public string? AgentObjective { get; init; }

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
            Active,
            AgentObjective);
}

/// <summary>A new routine: a name, an event type and actions; it starts off and not in test mode unless it says so.</summary>
public sealed class CreateRoutineDto : RoutineWriteDto;

public sealed class UpdateRoutineDto : RoutineWriteDto;

public sealed class SetRoutineActiveDto
{
    [Required(ErrorMessage = "RoutineActiveRequired")]
    public bool? Active { get; init; }
}

public sealed class SetRoutineTestModeDto
{
    [Required(ErrorMessage = "RoutineTestModeRequired")]
    public bool? TestMode { get; init; }
}

public sealed record RoutineInstructionDto([Required, StringLength(500)] string Instruction);

public sealed record RoutineActionSuggestion(string? Type);
