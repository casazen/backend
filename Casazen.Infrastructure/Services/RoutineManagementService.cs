using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// The routines of an org (E01/F04/US01/T03, NF-21, ADR-022): what the API of the routines page reads and writes. The engine that runs them is
/// <see cref="RoutineService"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The org</b> is in the predicate of every statement (the tenant filter of the scope is a second wall): a routine of another org is a 404
/// <c>routine_not_found</c>, for a read, a change, a switch, a delete and the log alike.</item>
/// <item><b>What is saved is what the engine can run</b>: the name (1 to 200 characters), the event type (a member of <see cref="RoutineEventType"/> that
/// something starts a routine on, <c>WeeklyRecurrence</c> not yet), the configuration of the event (<c>days</c> and <c>hours</c> 0 to 365 for the time
/// triggers, read by <see cref="TimeTrigger.OffsetHours"/> as the emitter does, nothing for the others), the condition (<see cref="IConditionEvaluator.ValidateCondition"/> and
/// the fields a routine can read, the kind of value each operator takes, no member nobody reads), 1 to 10 actions of a known type with a configuration that is an object of at most 4 KB and the rules of its type
/// (<see cref="RoutineActionRules"/>). Each refusal is a 422 with a stable code and a message in Italian and English.</item>
/// <item><b>Active means it can run</b>: outside test mode a routine is active only when every action is available (<see cref="RoutineActionCatalog"/>),
/// whichever way it gets there (the switch, test mode turned off, a change); in test mode it may hold any action.</item>
/// <item>At most 50 routines for an org (409 <c>routine_limit_reached</c>). A routine is created off, and not in test mode.</item>
/// <item>Names, texts and conditions are the host's own words: nothing of them is logged, only ids.</item>
/// </list>
/// </remarks>
public class RoutineManagementService(
    AppDbContext db,
    IConditionEvaluator conditionEvaluator,
    IFeatureFlags featureFlags,
    ILogger<RoutineManagementService> logger) : IRoutineManagementService
{
    private static readonly RoutineEventType[] TimeEvents = [.. TimeTrigger.StayKinds.Keys];

    /// <summary>Event types a routine can be saved for, but that nothing starts yet: the weekly recurrence has no job that writes it.</summary>
    private static readonly RoutineEventType[] EventsNotStartedYet = [RoutineEventType.WeeklyRecurrence];

    public async Task<IReadOnlyList<Routine>> ListAsync(Guid orgId, CancellationToken cancellationToken = default) =>
        await db.Routines
            .AsNoTracking()
            .Where(r => r.OrgId == orgId)
            .OrderBy(r => r.Name)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

    public async Task<Routine> GetAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default) =>
        await db.Routines
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, cancellationToken)
        ?? throw NotFound(id);

    public async Task<Routine> CreateAsync(Guid orgId, RoutineCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var definition = Define(command, existing: null);
        if (await db.Routines.CountAsync(r => r.OrgId == orgId, cancellationToken) >= RoutineLimits.MaxRoutinesPerOrg)
            throw new DomainConflictException(RoutineCodes.LimitReached, "RoutineLimitReached", RoutineLimits.MaxRoutinesPerOrg);

        var routine = new Routine
        {
            OrgId = orgId,
            AgentObjective = definition.AgentObjective,
            Name = definition.Name,
            EventType = definition.EventType,
            EventConfig = definition.EventConfig,
            ConditionJson = definition.ConditionJson,
            ActionsJson = definition.ActionsJson,
            TestMode = definition.TestMode,
            Active = definition.Active,
        };
        db.Routines.Add(routine);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Routine {RoutineId} created in org {OrgId} ({EventType}, active {Active}, test mode {TestMode})", routine.Id, orgId, routine.EventType, routine.Active, routine.TestMode);
        return routine;
    }

    public async Task<Routine> UpdateAsync(Guid orgId, Guid id, RoutineCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var routine = await FindAsync(orgId, id, cancellationToken);
        var definition = Define(command, routine);

        routine.AgentObjective = definition.AgentObjective;
        routine.Name = definition.Name;
        routine.EventType = definition.EventType;
        routine.EventConfig = definition.EventConfig;
        routine.ConditionJson = definition.ConditionJson;
        routine.ActionsJson = definition.ActionsJson;
        routine.TestMode = definition.TestMode;
        routine.Active = definition.Active;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Routine {RoutineId} of org {OrgId} changed", id, orgId);
        return routine;
    }

    public async Task DeleteAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default)
    {
        var routine = await FindAsync(orgId, id, cancellationToken);

        // The log goes with the routine: the foreign key of the executions deletes them (migration AddRoutineEngine).
        db.Routines.Remove(routine);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Routine {RoutineId} of org {OrgId} deleted", id, orgId);
    }

    public async Task<Routine> SetActiveAsync(Guid orgId, Guid id, bool active, CancellationToken cancellationToken = default)
    {
        var routine = await FindAsync(orgId, id, cancellationToken);
        if (active && !routine.TestMode)
            RequireAvailableActions(StoredActions(routine));

        routine.Active = active;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Routine {RoutineId} of org {OrgId} switched {State}", id, orgId, active ? "on" : "off");
        return routine;
    }

    public async Task<Routine> SetTestModeAsync(Guid orgId, Guid id, bool testMode, CancellationToken cancellationToken = default)
    {
        var routine = await FindAsync(orgId, id, cancellationToken);
        if (!testMode && routine.Active)
            RequireAvailableActions(StoredActions(routine));

        routine.TestMode = testMode;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Routine {RoutineId} of org {OrgId}: test mode {State}", id, orgId, testMode ? "on" : "off");
        return routine;
    }

    public async Task<RoutineExecutionPage> ListExecutionsAsync(
        Guid orgId,
        Guid routineId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (!await db.Routines.AnyAsync(r => r.Id == routineId && r.OrgId == orgId, cancellationToken))
            throw NotFound(routineId);

        // The page is bound so that the rows to skip, (page - 1) * size, fit an int: a page that no log reaches is an empty page, never an overflow
        // (a negative OFFSET is an error of the database, a 500).
        var size = Math.Clamp(pageSize, 1, RoutineLimits.MaxExecutionsPageSize);
        var number = Math.Clamp(page, 1, int.MaxValue / size);
        var executions = db.RoutineExecutions
            .AsNoTracking()
            .Where(e => e.OrgId == orgId && e.RoutineId == routineId);

        var total = await executions.CountAsync(cancellationToken);
        var items = await executions
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        return new RoutineExecutionPage(items, total, number, size);
    }

    /// <summary>The routine of the org, tracked for a change; 404 when it is not the org's.</summary>
    private async Task<Routine> FindAsync(Guid orgId, Guid id, CancellationToken cancellationToken) =>
        await db.Routines.FirstOrDefaultAsync(r => r.Id == id && r.OrgId == orgId, cancellationToken)
        ?? throw NotFound(id);

    // ─── The routine that results: validated as a whole ─────────────────────────────────────────────────────

    /// <summary>The routine a command makes: its members, validated, with the active and test-mode rule applied.</summary>
    private sealed record Definition(
        string Name,
        RoutineEventType EventType,
        string EventConfig,
        string ConditionJson,
        string ActionsJson,
        bool TestMode,
        bool Active,
        string? AgentObjective);

    /// <summary>
    /// The routine that <paramref name="command"/> makes of <paramref name="existing"/> (a new one when there is none): what the command leaves out
    /// stays as it is, and the result is validated whole, so a change of the event type is judged with the configuration the routine keeps.
    /// </summary>
    private Definition Define(RoutineCommand command, Routine? existing)
    {
        var name = ValidName(command.Name ?? existing?.Name);

        var eventType = command.EventType is { } rawType
            ? ParseEventType(rawType)
            : existing?.EventType ?? throw new DomainRuleException(RoutineCodes.EventTypeInvalid, "RoutineEventTypeInvalid");
        if (EventsNotStartedYet.Contains(eventType))
            throw new DomainRuleException(RoutineCodes.EventNotAvailable, "RoutineEventNotAvailable");

        var eventConfig = command.EventConfig is { } configuration ? ConfigurationText(configuration) : existing?.EventConfig ?? "{}";
        ValidateEventConfig(eventType, eventConfig);

        var conditionJson = command.Condition is { } condition ? ConditionText(condition) : existing?.ConditionJson ?? "{}";
        ValidateCondition(conditionJson);

        string actionsJson;
        IReadOnlyList<RoutineAction> actions;
        if (command.Actions is { } inputs)
            (actionsJson, actions) = ReadActions(inputs);
        else if (existing is not null)
            (actionsJson, actions) = (existing.ActionsJson, StoredActions(existing));
        else
            throw ActionsInvalid();

        var testMode = command.TestMode ?? existing?.TestMode ?? false;
        var active = command.Active ?? existing?.Active ?? false;
        if (active && !testMode)
            RequireAvailableActions(actions);

        var objective = (command.AgentObjective ?? existing?.AgentObjective)?.Trim();
        if (objective?.Length > 500)
            throw new DomainRuleException("agent_objective_invalid", "AgentObjectiveInvalid");

        return new Definition(name, eventType, eventConfig, string.IsNullOrWhiteSpace(conditionJson) ? "{}" : conditionJson, actionsJson, testMode, active, string.IsNullOrEmpty(objective) ? null : objective);
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim();
        return trimmed is { Length: > 0 and <= RoutineLimits.MaxNameLength }
            ? trimmed
            : throw new DomainRuleException(RoutineCodes.NameInvalid, "RoutineNameInvalid", RoutineLimits.MaxNameLength);
    }

    private static RoutineEventType ParseEventType(string raw) =>
        EnumNames.TryParseDefined<RoutineEventType>(raw, out var type)
            ? type
            : throw new DomainRuleException(RoutineCodes.EventTypeInvalid, "RoutineEventTypeInvalid");

    /// <summary>The configuration of an event as written: <c>null</c> and the JSON <c>null</c> are no configuration.</summary>
    private static string ConfigurationText(JsonElement configuration) =>
        configuration.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "{}" : configuration.GetRawText();

    /// <summary>The condition as written: an empty object is no condition, the JSON <c>null</c> is none either.</summary>
    private static string ConditionText(JsonElement condition) =>
        condition.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
        || (condition.ValueKind == JsonValueKind.Object && !condition.EnumerateObject().Any())
            ? "{}"
            : condition.GetRawText();

    /// <summary>
    /// <c>{ "days": N, "hours": N }</c>, each a whole number of 0 to 365 and together an offset <see cref="TimeTrigger.OffsetHours"/> reads, for a time
    /// trigger; an empty object for any other type. Anything else is a 422: the emitter and the handler read the same text, so what is saved is what fires.
    /// </summary>
    private static void ValidateEventConfig(RoutineEventType type, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > RoutineLimits.MaxConfigBytes)
            throw EventConfigInvalid();

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw EventConfigInvalid();

            var members = document.RootElement.EnumerateObject().ToList();
            if (!TimeEvents.Contains(type))
            {
                if (members.Count > 0)
                    throw EventConfigInvalid();

                return;
            }

            foreach (var member in members)
            {
                var known = string.Equals(member.Name, "days", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(member.Name, "hours", StringComparison.OrdinalIgnoreCase);
                if (!known
                    || member.Value.ValueKind != JsonValueKind.Number
                    || !member.Value.TryGetInt32(out var number)
                    || number is < 0 or > RoutineLimits.MaxEventConfigMember)
                {
                    throw EventConfigInvalid();
                }
            }

            if (TimeTrigger.OffsetHours(json) is null)
                throw EventConfigInvalid();
        }
        catch (JsonException)
        {
            throw EventConfigInvalid();
        }
    }

    /// <summary>The condition read as the engine reads it and checked by the evaluator, with the fields a routine can read and the kind of value each operator takes (<see cref="RoutineConditions.ValueErrors"/>); 422 with the errors.</summary>
    private void ValidateCondition(string conditionJson)
    {
        if (Encoding.UTF8.GetByteCount(conditionJson) > RoutineLimits.MaxConditionBytes)
            throw ConditionInvalid(["The condition is too large"]);

        var parsed = RoutineConditions.Parse(conditionJson);
        if (!parsed.Valid)
            throw ConditionInvalid(["The condition is not a condition: only version and groups (operator and rules, each with fieldId, operator and value) are read"]);

        if (parsed.Condition is not { } condition)
            return;

        var errors = conditionEvaluator.ValidateCondition(condition)
            .Concat(RoutineConditions.UnsupportedFieldErrors(condition))
            .Concat(RoutineConditions.ValueErrors(condition))
            .ToList();
        if (errors.Count > 0)
            throw ConditionInvalid(errors);
    }

    /// <summary>The actions of a command: 1 to 10, each of a known type with a configuration that is an object of at most 4 KB and valid for its type.</summary>
    private static (string ActionsJson, IReadOnlyList<RoutineAction> Actions) ReadActions(IReadOnlyList<RoutineActionInput> inputs)
    {
        if (inputs.Count is 0 or > RoutineLimits.MaxActions)
            throw ActionsInvalid();

        var actions = new List<RoutineAction>();
        var items = new List<object>();
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index] ?? throw ActionsInvalid(index);
            if (!EnumNames.TryParseDefined<RoutineActionType>(input.Type, out var type))
                throw ActionsInvalid(index);

            var config = input.Config is { } given && given.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? given
                : JsonSerializer.SerializeToElement(new { });
            if (config.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(config.GetRawText()) > RoutineLimits.MaxConfigBytes)
                throw ActionConfigInvalid(index, type);

            var item = new { type = type.ToString(), config };
            if (!RoutineAction.TryParse(JsonSerializer.SerializeToElement(item), out var action))
                throw ActionsInvalid(index);

            if (!RoutineActionRules.IsValidConfig(action))
                throw ActionConfigInvalid(index, type);

            actions.Add(action);
            items.Add(item);
        }

        return (JsonSerializer.Serialize(items), actions);
    }

    /// <summary>The actions of a saved routine; 422 when they cannot be read (a routine saved some other way).</summary>
    private static IReadOnlyList<RoutineAction> StoredActions(Routine routine) =>
        RoutineAction.TryParseList(routine.ActionsJson, out var actions) ? actions : throw ActionsInvalid();

    /// <summary>Outside test mode a routine that is active holds only actions the catalog lists as available; 422 naming the others.</summary>
    private void RequireAvailableActions(IReadOnlyList<RoutineAction> actions)
    {
        var unavailable = actions
            .Where(action => !RoutineActionCatalog.IsAvailable(action.Type, featureFlags))
            .Select(action => action.Type.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (unavailable.Count == 0)
        {
            for (var index = 0; index < actions.Count; index++)
            {
                if (!RoutineActionRules.HasRequiredFields(actions[index]))
                    throw ActionConfigInvalid(index, actions[index].Type);
            }
            return;
        }

        throw new DomainRuleException(RoutineCodes.ActionNotAvailable, "RoutineActionNotAvailable", string.Join(", ", unavailable))
        {
            Extensions = new Dictionary<string, object?> { ["actions"] = unavailable },
        };
    }

    // ─── What is refused ─────────────────────────────────────────────────────────────────────────────────────

    private static NotFoundException NotFound(Guid id) =>
        new($"Routine {id} not found") { Code = RoutineCodes.NotFound, MessageKey = "RoutineNotFound" };

    private static DomainRuleException EventConfigInvalid() =>
        new(RoutineCodes.EventConfigInvalid, "RoutineEventConfigInvalid", RoutineLimits.MaxEventConfigMember);

    private static DomainRuleException ConditionInvalid(IReadOnlyList<string> errors) =>
        new(RoutineCodes.ConditionInvalid, "RoutineConditionInvalid")
        {
            Extensions = new Dictionary<string, object?> { ["errors"] = errors },
        };

    private static DomainRuleException ActionsInvalid(int? index = null) =>
        new(RoutineCodes.ActionsInvalid, "RoutineActionsInvalid", RoutineLimits.MaxActions)
        {
            Extensions = index is { } position
                ? new Dictionary<string, object?> { ["actionIndex"] = position }
                : new Dictionary<string, object?>(),
        };

    private static DomainRuleException ActionConfigInvalid(int index, RoutineActionType type) =>
        new(RoutineCodes.ActionConfigInvalid, "RoutineActionConfigInvalid", index + 1, type.ToString())
        {
            Extensions = new Dictionary<string, object?> { ["actionIndex"] = index, ["type"] = type.ToString() },
        };
}
