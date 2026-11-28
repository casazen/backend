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
    public const string AiPlanInvalid = "routine_ai_plan_invalid";
    public const string AiNotSelected = "routine_ai_not_selected";
    public const string AiSubscriptionRequired = "routine_ai_subscription_required";
    public const string AiFactsMissing = "routine_ai_condition_unknown";
    public const string AiConditionFalse = "routine_ai_condition_false";
    public const string AiUnavailable = "routine_ai_unavailable";

    public const string ConditionInvalid = "routine_condition_invalid";

    public const string ActionNotAvailable = "routine_action_not_available";

    public const string ActionsInvalid = "routine_actions_invalid";

    public const string Inactive = "routine_inactive";

    public const string ActionError = "routine_action_error";

    public const string WaitNotScheduled = "routine_wait_not_scheduled";

    public const string NotFound = "routine_not_found";

    public const string ActivityNeedsBooking = "routine_activity_needs_booking";

    public const string CleaningNeedsBooking = "routine_cleaning_needs_booking";

    public const string CleaningNotConfigured = "routine_cleaning_not_configured";

    public const string ReviewNeedsBooking = "routine_review_needs_booking";

    public const string ReviewInviteNotSent = "review_invite_not_sent";

    public const string BookingNotActive = "routine_booking_not_active";

    public const string NameInvalid = "routine_name_invalid";

    public const string EventTypeInvalid = "routine_event_type_invalid";

    public const string EventNotAvailable = "routine_event_not_available";

    public const string EventConfigInvalid = "routine_event_config_invalid";

    public const string ActionConfigInvalid = "routine_action_config_invalid";

    public const string LimitReached = "routine_limit_reached";

    public const string SimulationPayloadInvalid = "routine_payload_invalid";

    public const string NeedsOrgScope = "routines_need_org_scope";

    public const string AccessCodeNeedsIdVerification = "access_code_needs_id_verification";

    public const string AirbnbNotConfirmed = "airbnb_not_confirmed";

    public const string CampaignNeedsConsent = "campaign_needs_consent";
}

public static class RoutineExecutionOutcomes
{
    public const string Pending = "Pending";

    public const string Running = "Running";

    public const string Waiting = "Waiting";

    public const string Success = "Success";

    public const string Failed = "Failed";
}

public static class RoutineActionOutcomes
{
    public const string Done = "done";

    public const string Simulated = "simulated";

    public const string Blocked = "blocked";

    public const string Skipped = "skipped";

    public const string Failed = "failed";

    public const string Waiting = "waiting";
}

/// <summary>What one action of a routine did (E01/F04/US01/T01): its place in the list, its type and how it ended.</summary>
/// <param name="Index">The position of the action in the routine, from 0.</param>
/// <param name="Type">The type of the action.</param>
/// <param name="Outcome">One of <see cref="RoutineActionOutcomes"/>.</param>
/// <param name="Code">Why: the guardrail of a blocked action, the reason of a skipped one, the stable code of a failed one; <c>null</c> otherwise.</param>
public sealed record RoutineActionResult(int Index, RoutineActionType Type, string Outcome, string? Code)
{
    public static RoutineActionResult Done(int index, RoutineActionType type) => new(index, type, RoutineActionOutcomes.Done, null);

    public static RoutineActionResult Simulated(int index, RoutineActionType type) => new(index, type, RoutineActionOutcomes.Simulated, null);

    public static RoutineActionResult Blocked(int index, RoutineActionType type, string code) => new(index, type, RoutineActionOutcomes.Blocked, code);

    public static RoutineActionResult Skipped(int index, RoutineActionType type, string code) => new(index, type, RoutineActionOutcomes.Skipped, code);

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

    public static RoutineExecutionResult Empty => new([], null, 0);

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

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

    public static string WithoutPayload(string? json) => (Parse(json) with { PayloadJson = null }).ToJson();
}

public sealed class RoutineAction
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoConfig = new Dictionary<string, JsonElement>();

    private RoutineAction(RoutineActionType type, IReadOnlyDictionary<string, JsonElement> config)
    {
        Type = type;
        Config = config;
    }

    public RoutineActionType Type { get; }

    public IReadOnlyDictionary<string, JsonElement> Config { get; }

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

    public bool TryGetInt(string name, out int value)
    {
        value = 0;
        return Config.TryGetValue(name, out var member)
               && member.ValueKind == JsonValueKind.Number
               && member.TryGetInt32(out value);
    }

    public string? GetString(string name) =>
        Config.TryGetValue(name, out var member) && member.ValueKind == JsonValueKind.String ? member.GetString() : null;

    public bool IsOnUnlessFalse(string name) =>
        Config.TryGetValue(name, out var member) && member.ValueKind != JsonValueKind.False;

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

public sealed record RoutineSimulatedAction(int Index, RoutineActionType Type, JsonElement Config, bool WouldBlock, string? Code);
