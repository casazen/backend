using System.Text.Json;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>The limits of the routines of an org and of what the API of the routines accepts (E01/F04/US01/T03).</summary>
public static class RoutineLimits
{
    /// <summary>The most routines an org can have; the next one is a 409 <c>routine_limit_reached</c>.</summary>
    public const int MaxRoutinesPerOrg = 50;

    /// <summary>The longest name of a routine.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The most actions a routine has; a routine has at least one.</summary>
    public const int MaxActions = 10;

    /// <summary>The largest configuration of an action or of an event, in bytes of its JSON.</summary>
    public const int MaxConfigBytes = 4096;

    /// <summary>The largest condition, in bytes of its JSON.</summary>
    public const int MaxConditionBytes = 16384;

    /// <summary>The most a <c>days</c> or <c>hours</c> of a time trigger is (the offset altogether is bound by <c>TimeTrigger.MaxOffsetHours</c>).</summary>
    public const int MaxEventConfigMember = 365;

    /// <summary>The page of the log of a routine when the caller does not say.</summary>
    public const int DefaultExecutionsPageSize = 50;

    /// <summary>The most executions a page of the log holds.</summary>
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
public sealed record RoutineCommand(
    string? Name,
    string? EventType,
    JsonElement? EventConfig,
    JsonElement? Condition,
    IReadOnlyList<RoutineActionInput>? Actions,
    bool? TestMode,
    bool? Active);

/// <summary>One action of a routine as a caller writes it: the type by name and its configuration.</summary>
/// <param name="Type">The name of a <see cref="Entities.Enums.RoutineActionType"/> member.</param>
/// <param name="Config">The configuration, a JSON object of at most <see cref="RoutineLimits.MaxConfigBytes"/> bytes; none is an empty one.</param>
public sealed record RoutineActionInput(string? Type, JsonElement? Config);

/// <summary>A page of the log of a routine, the newest execution first.</summary>
/// <param name="Items">The executions of the page.</param>
/// <param name="TotalCount">All the executions of the routine.</param>
/// <param name="Page">The page, from 1.</param>
/// <param name="PageSize">The size of the page.</param>
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
    /// <summary>The routines of the org, by name.</summary>
    Task<IReadOnlyList<Routine>> ListAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>One routine of the org.</summary>
    Task<Routine> GetAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a routine from a command that says its name, event type and actions. 422 for what is not valid, 409 <c>routine_limit_reached</c> at
    /// <see cref="RoutineLimits.MaxRoutinesPerOrg"/> routines. Not active and not in test mode unless the command says so.
    /// </summary>
    Task<Routine> CreateAsync(Guid orgId, RoutineCommand command, CancellationToken cancellationToken = default);

    /// <summary>Changes a routine: the members the command leaves out stay as they are, and the whole routine that results is validated.</summary>
    Task<Routine> UpdateAsync(Guid orgId, Guid id, RoutineCommand command, CancellationToken cancellationToken = default);

    /// <summary>Deletes a routine and, with it, its log.</summary>
    Task DeleteAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Switches a routine on or off. On, outside test mode, only when every action is available.</summary>
    Task<Routine> SetActiveAsync(Guid orgId, Guid id, bool active, CancellationToken cancellationToken = default);

    /// <summary>Switches test mode on or off. Off, while the routine is active, only when every action is available.</summary>
    Task<Routine> SetTestModeAsync(Guid orgId, Guid id, bool testMode, CancellationToken cancellationToken = default);

    /// <summary>A page of the log of a routine, the newest first. <paramref name="page"/> from 1; <paramref name="pageSize"/> 1 to <see cref="RoutineLimits.MaxExecutionsPageSize"/>.</summary>
    Task<RoutineExecutionPage> ListExecutionsAsync(
        Guid orgId,
        Guid routineId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
