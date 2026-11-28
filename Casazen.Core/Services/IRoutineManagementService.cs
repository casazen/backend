using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

public static class RoutineLimits
{
    public const int MaxRoutinesPerOrg = 50;

    public const int MaxNameLength = 200;

    public const int MaxActions = 10;

    public const int MaxConfigBytes = 4096;

    public const int MaxConditionBytes = 16384;

    public const int MaxEventConfigMember = 365;

    public const int DefaultExecutionsPageSize = 50;

    public const int MaxExecutionsPageSize = 100;
}

/// <summary>
/// What a caller says of a routine, to create it (every member the routine needs) or to change it (the members it leaves out stay as they are). The
/// names are the ones of the frontend client; nothing here is trusted: the service validates the whole routine that results.
/// </summary>
/// <param name="Name">1 to <see cref="RoutineLimits.MaxNameLength"/> characters.</param>
/// <param name="EventType">The name of a <see cref="Entities.Enums.RoutineEventType"/> member; refused with a code, never a guess, when it is not one.</param>
/// <param name="EventConfig">The configuration of the event: <c>{ "days": N, "hours": N }</c> for a time trigger, nothing for the others.</param>
/// <param name="Condition">The condition of ADR-014; an empty object is "no condition".</param>
/// <param name="Actions">1 to <see cref="RoutineLimits.MaxActions"/> actions, each <c>{ type, config }</c>.</param>
/// <param name="TestMode">Test mode: the routine simulates its actions (new routines: off).</param>
/// <param name="Active">Whether the routine runs (new routines: off).</param>
/// <param name="AgentObjective">Instructions for selecting the configured actions for each event.</param>
public sealed record RoutineCommand(
    string? Name,
    string? EventType,
    JsonElement? EventConfig,
    JsonElement? Condition,
    IReadOnlyList<RoutineActionInput>? Actions,
    bool? TestMode,
    bool? Active,
    string? AgentObjective = null);

public sealed record RoutineActionInput(string? Type, JsonElement? Config);

public sealed record RoutineExecutionPage(IReadOnlyList<RoutineExecution> Items, int TotalCount, int Page, int PageSize);

/// <summary>
/// The routines of an org: list, read, create, change, delete, switch on and off, switch test mode, and read the log (E01/F04/US01/T03, NF-21,
/// ADR-022). The engine that runs them stays <see cref="IRoutineService"/>.
/// </summary>
/// <remarks>
/// Every statement names the org: a routine of another org is a <see cref="Exceptions.NotFoundException"/> (<c>routine_not_found</c>), never a
/// 403, whatever is asked. A routine acts on every property of the org, so the web layer lets only a caller who reaches the whole org change one.
/// What is refused is refused with a <see cref="Exceptions.DomainRuleException"/> (422) and a stable code of <c>RoutineCodes</c>, in Italian and
/// English: the name, the event type, the configuration of the event, the condition, the actions and their configuration, and a routine that is
/// active outside test mode with an action that is not available (<c>RoutineActionCatalog</c>).
/// </remarks>
public interface IRoutineManagementService
{
    Task<IReadOnlyList<Routine>> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<Routine> GetAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a routine from a command that says its name, event type and actions. 422 for what is not valid, 409 <c>routine_limit_reached</c> at
    /// <see cref="RoutineLimits.MaxRoutinesPerOrg"/> routines. Not active and not in test mode unless the command says so.
    /// </summary>
    Task<Routine> CreateAsync(Guid orgId, RoutineCommand command, CancellationToken cancellationToken = default);

    Task<Routine> UpdateAsync(Guid orgId, Guid id, RoutineCommand command, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default);

    Task<Routine> SetActiveAsync(Guid orgId, Guid id, bool active, CancellationToken cancellationToken = default);

    Task<Routine> SetTestModeAsync(Guid orgId, Guid id, bool testMode, CancellationToken cancellationToken = default);

    Task<RoutineExecutionPage> ListExecutionsAsync(
        Guid orgId,
        Guid routineId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
