using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Validation;

namespace Casazen.Core.Models;

/// <summary>
/// The stable codes of the routine engine (E01/F04/US01, NF-21, ADR-022): what an execution or an action answers instead of a text. The
/// interface translates them, so a code is never renamed and never carries a name, an address or any other personal data.
/// </summary>
public static class RoutineCodes
{
    /// <summary>The condition of the routine is not a valid one (a field routines do not know, an operator the field does not take, a value missing): the routine does not run and its execution says so.</summary>
    public const string ConditionInvalid = "routine_condition_invalid";

    /// <summary>The action has no implementation yet, or the feature it needs is off (<c>RoutineActionCatalog</c>): it fails, it never pretends to have worked.</summary>
    public const string ActionNotAvailable = "routine_action_not_available";

    /// <summary>The actions saved in the routine cannot be read, or one of them has a configuration the engine refuses.</summary>
    public const string ActionsInvalid = "routine_actions_invalid";

    /// <summary>The routine was deactivated while an execution waited: the execution ends, it does not continue.</summary>
    public const string Inactive = "routine_inactive";

    /// <summary>An action failed in a way nobody expected (the database, the queue): the execution is closed with this code and the failure goes on to the dispatcher, which delivers the event again.</summary>
    public const string ActionError = "routine_action_error";

    /// <summary>The job that resumes a Wait could not be scheduled: the execution cannot continue, so it is closed with this code.</summary>
    public const string WaitNotScheduled = "routine_wait_not_scheduled";

    /// <summary>The routine does not exist in the org of the caller (a routine of another org is the same 404).</summary>
    public const string NotFound = "routine_not_found";

    /// <summary><c>CreateActivity</c> needs the booking of the event: the event names none, or the org has no such booking.</summary>
    public const string ActivityNeedsBooking = "routine_activity_needs_booking";

    /// <summary>
    /// The booking of the event was cancelled, or marked as a no-show, while the execution waited: the actions after the wait are not run (a stay that does not
    /// happen gets no To do and no message). Recorded as the reason of each action left, <c>blocked</c>; the routines of a cancellation are not stopped by it.
    /// </summary>
    public const string BookingNotActive = "routine_booking_not_active";

    /// <summary>API, 422: the name of a routine is empty or longer than <see cref="RoutineLimits.MaxNameLength"/>.</summary>
    public const string NameInvalid = "routine_name_invalid";

    /// <summary>API, 422: the event type is not a member of <see cref="Casazen.Core.Entities.Enums.RoutineEventType"/>.</summary>
    public const string EventTypeInvalid = "routine_event_type_invalid";

    /// <summary>API, 422: the event type exists but nothing starts a routine on it yet (<c>WeeklyRecurrence</c>: no job writes it).</summary>
    public const string EventNotAvailable = "routine_event_not_available";

    /// <summary>API, 422: the configuration of the event is not valid for its type (days and hours 0 to 365 for the time triggers, nothing for the others).</summary>
    public const string EventConfigInvalid = "routine_event_config_invalid";

    /// <summary>API, 422: the configuration of an action is not valid for its type (a wait out of range, a To do with no text…).</summary>
    public const string ActionConfigInvalid = "routine_action_config_invalid";

    /// <summary>API, 409: the org already has <see cref="RoutineLimits.MaxRoutinesPerOrg"/> routines.</summary>
    public const string LimitReached = "routine_limit_reached";

    /// <summary>API, 422: the sample payload of a simulation is not a JSON object.</summary>
    public const string SimulationPayloadInvalid = "routine_payload_invalid";

    /// <summary>API, 403: a routine acts on every property of the org, so who changes one reaches the whole org.</summary>
    public const string NeedsOrgScope = "routines_need_org_scope";

    /// <summary>Guardrail (ADR-022-R11): an access code goes out only once the identity of the guest is verified.</summary>
    public const string AccessCodeNeedsIdVerification = "access_code_needs_id_verification";

    /// <summary>Guardrail (ADR-022-R11): an Airbnb guest is not contacted before the booking is confirmed.</summary>
    public const string AirbnbNotConfirmed = "airbnb_not_confirmed";

    /// <summary>Guardrail (ADR-022-R11): a campaign goes only to a guest who consented to marketing.</summary>
    public const string CampaignNeedsConsent = "campaign_needs_consent";
}

/// <summary>The values of <c>RoutineExecution.Outcome</c> (a string column).</summary>
public static class RoutineExecutionOutcomes
{
    /// <summary>Created and not started (the default of the column).</summary>
    public const string Pending = "Pending";

    /// <summary>The actions are running.</summary>
    public const string Running = "Running";

    /// <summary>A Wait is pending: <c>ResumeAt</c> is when the job that continues it runs.</summary>
    public const string Waiting = "Waiting";

    /// <summary>Every action ended done, simulated or blocked.</summary>
    public const string Success = "Success";

    /// <summary>The condition was invalid, the routine was deactivated while it waited, or an action failed.</summary>
    public const string Failed = "Failed";
}

/// <summary>The values of <see cref="RoutineActionResult.Outcome"/>.</summary>
public static class RoutineActionOutcomes
{
    /// <summary>The action ran and did what it says.</summary>
    public const string Done = "done";

    /// <summary>The routine is in test mode: nothing ran, nothing changed.</summary>
    public const string Simulated = "simulated";

    /// <summary>A guardrail stopped the action (<see cref="RoutineActionResult.Code"/> is its reason); the next actions go on.</summary>
    public const string Blocked = "blocked";

    /// <summary>The action could not be done (<see cref="RoutineActionResult.Code"/> is a stable code); the actions after it do not run.</summary>
    public const string Failed = "failed";

    /// <summary>A Wait: the execution continues at its <c>ResumeAt</c>. Becomes <see cref="Done"/> when the wait is over.</summary>
    public const string Waiting = "waiting";
}

/// <summary>What one action of a routine did (E01/F04/US01/T01): its place in the list, its type and how it ended.</summary>
/// <param name="Index">The position of the action in the routine, from 0.</param>
/// <param name="Type">The type of the action.</param>
/// <param name="Outcome">One of <see cref="RoutineActionOutcomes"/>.</param>
/// <param name="Code">Why: the guardrail of a blocked action, the stable code of a failed one; <c>null</c> otherwise.</param>
public sealed record RoutineActionResult(int Index, RoutineActionType Type, string Outcome, string? Code)
{
    public static RoutineActionResult Done(int index, RoutineActionType type) => new(index, type, RoutineActionOutcomes.Done, null);

    public static RoutineActionResult Simulated(int index, RoutineActionType type) => new(index, type, RoutineActionOutcomes.Simulated, null);

    public static RoutineActionResult Blocked(int index, RoutineActionType type, string code) => new(index, type, RoutineActionOutcomes.Blocked, code);

    public static RoutineActionResult Failed(int index, RoutineActionType type, string code) => new(index, type, RoutineActionOutcomes.Failed, code);

    public static RoutineActionResult Waiting(int index, RoutineActionType type) => new(index, type, RoutineActionOutcomes.Waiting, null);
}

/// <summary>
/// The result of an execution, kept in <c>RoutineExecution.ResultJson</c> (camelCase, enums by name, nothing written for a <c>null</c>):
/// <c>{ "actions": [{ "index", "type", "outcome", "code"? }], "payloadJson"?, "nextActionIndex", "code"? }</c>.
/// </summary>
/// <param name="Actions">What each action that ran did, in order.</param>
/// <param name="PayloadJson">While a Wait is pending: the payload of the event (ids and codes, never personal data, ADR-022-R21), so the execution continues with what the event said.</param>
/// <param name="NextActionIndex">The action the execution continues at after the wait (its index in the list); the end of the list when nothing is left.</param>
/// <param name="Code">Why the execution as a whole failed, when it did not fail in an action: <see cref="RoutineCodes.ConditionInvalid"/>, <see cref="RoutineCodes.Inactive"/>…</param>
public sealed record RoutineExecutionResult(
    IReadOnlyList<RoutineActionResult> Actions,
    string? PayloadJson,
    int NextActionIndex,
    string? Code = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>An execution that did nothing yet.</summary>
    public static RoutineExecutionResult Empty => new([], null, 0);

    /// <summary>The JSON kept in <c>ResultJson</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// The result kept in <paramref name="json"/>. Never throws: what is not a result (the <c>{}</c> a new execution starts with, a text
    /// written before this shape existed, a type no member declares) is an <see cref="Empty"/> one.
    /// </summary>
    public static RoutineExecutionResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<RoutineExecutionResult>(json, JsonOptions);
            return parsed is null ? Empty : parsed with { Actions = parsed.Actions ?? [] };
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// The result as the host reads it in the log of a routine: the saved payload is the engine's own state to continue a wait, not something to
    /// show, so it is left out.
    /// </summary>
    public static string WithoutPayload(string? json) => (Parse(json) with { PayloadJson = null }).ToJson();
}

/// <summary>
/// One action of a routine as the engine reads it from <c>Routine.ActionsJson</c>: <c>{ "type": "CreateActivity", "config": { … } }</c>, the type by name
/// and the configuration an object (the names of its members are read in any case). Built only by <see cref="TryParseList"/>.
/// </summary>
public sealed class RoutineAction
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoConfig = new Dictionary<string, JsonElement>();

    private RoutineAction(RoutineActionType type, IReadOnlyDictionary<string, JsonElement> config)
    {
        Type = type;
        Config = config;
    }

    /// <summary>The type of the action.</summary>
    public RoutineActionType Type { get; }

    /// <summary>The members of the configuration, by name in any case.</summary>
    public IReadOnlyDictionary<string, JsonElement> Config { get; }

    /// <summary>The configuration as the JSON object it was written as (an empty object when there is none).</summary>
    public JsonElement ConfigElement => JsonSerializer.SerializeToElement(Config);

    /// <summary>
    /// Reads the actions of a routine. False when <paramref name="json"/> is not a JSON array of objects with a <c>type</c> that names a
    /// <see cref="RoutineActionType"/> member (a number does not) and a <c>config</c> that is an object or missing: the routine is then
    /// not run, it never runs "what could be read". <c>[]</c> is a valid list with no action.
    /// </summary>
    public static bool TryParseList(string? json, out IReadOnlyList<RoutineAction> actions)
    {
        actions = [];
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var parsed = new List<RoutineAction>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!TryParse(item, out var action))
                    return false;

                parsed.Add(action);
            }

            actions = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads one action; false when it is not an object with a known <c>type</c> and a <c>config</c> that is an object or missing.</summary>
    public static bool TryParse(JsonElement element, out RoutineAction action)
    {
        action = null!;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        string? typeName = null;
        IReadOnlyDictionary<string, JsonElement> config = NoConfig;
        foreach (var member in element.EnumerateObject())
        {
            if (string.Equals(member.Name, "type", StringComparison.OrdinalIgnoreCase))
            {
                if (member.Value.ValueKind != JsonValueKind.String)
                    return false;

                typeName = member.Value.GetString();
            }
            else if (string.Equals(member.Name, "config", StringComparison.OrdinalIgnoreCase)
                     && member.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (member.Value.ValueKind != JsonValueKind.Object)
                    return false;

                var members = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in member.Value.EnumerateObject())
                    members[entry.Name] = entry.Value.Clone();
                config = members;
            }
        }

        if (!EnumNames.TryParseDefined<RoutineActionType>(typeName, out var type))
            return false;

        action = new RoutineAction(type, config);
        return true;
    }

    /// <summary>A whole number member of the configuration; false when it is missing or is not a whole number that fits an <see cref="int"/>.</summary>
    public bool TryGetInt(string name, out int value)
    {
        value = 0;
        return Config.TryGetValue(name, out var member)
               && member.ValueKind == JsonValueKind.Number
               && member.TryGetInt32(out value);
    }

    /// <summary>A text member of the configuration; <c>null</c> when it is missing or is not a JSON string.</summary>
    public string? GetString(string name) =>
        Config.TryGetValue(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    /// <summary>A switch of the configuration: true only for the JSON <c>true</c>.</summary>
    public bool GetFlag(string name) =>
        Config.TryGetValue(name, out var member) && member.ValueKind == JsonValueKind.True;
}

/// <summary>What a routine would do for a sample payload, written by nothing and sent to nobody (E01/F04/US01/T01, AC-NF26-7).</summary>
/// <param name="RoutineId">The routine.</param>
/// <param name="RoutineName">Its name.</param>
/// <param name="ConditionsMatched">Whether its condition is met by the booking the sample payload names (always true for no condition).</param>
/// <param name="WouldExecute">Whether it would run: the condition is valid and met.</param>
/// <param name="Actions">Each action with whether it would be stopped (a guardrail, or no implementation).</param>
/// <param name="Code">Why the routine would not run at all: <see cref="RoutineCodes.ConditionInvalid"/> or <see cref="RoutineCodes.ActionsInvalid"/>; <c>null</c> otherwise.</param>
public sealed record RoutineSimulationResult(
    Guid RoutineId,
    string RoutineName,
    bool ConditionsMatched,
    bool WouldExecute,
    IReadOnlyList<RoutineSimulatedAction> Actions,
    string? Code = null);

/// <summary>An action in <see cref="RoutineSimulationResult"/>.</summary>
/// <param name="Index">The position of the action, from 0.</param>
/// <param name="Type">Its type.</param>
/// <param name="Config">Its configuration.</param>
/// <param name="WouldBlock">It would not run: a guardrail stops it, or it has no implementation.</param>
/// <param name="Code">The reason of <paramref name="WouldBlock"/>; <c>null</c> when it would run.</param>
public sealed record RoutineSimulatedAction(int Index, RoutineActionType Type, JsonElement Config, bool WouldBlock, string? Code);
