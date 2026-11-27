using Casazen.Core.Assistant;
using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The assistant turns a message of a guest into a draft that a person approves (E05/F05/US02/T01, NF-66, NF-67, PO-08, PO-09): it classifies the message, finds the facts, writes
/// the draft, scores it, records the decision and puts the draft in the approval queue (or, once a real gate is registered, sends it by itself). Behind
/// <c>Features:MessageAssistant</c>: the controllers and the job check the flag, the service does not.
/// </summary>
/// <remarks>
/// <para><b>Once per message.</b> The decision log has one row per message (<c>AssistantDecisionLog.InboundMessageId</c>, unique): processing a message twice finds the first row and
/// does nothing; only a person who asks for a new draft (<see cref="DraftNowAsync"/>) writes the row again.</para>
/// <para><b>No AI text leaves unapproved.</b> Until a real <see cref="IAutoSendGate"/> is registered (E05/F05/US03), every draft goes to the approval queue and nothing is sent by itself.</para>
/// <para><b>Personal data (PO-09).</b> The prompts carry placeholders instead of names, contact data, codes and long numbers (<see cref="AssistantPromptBuilder.Minimize"/>); the log keeps
/// ids, codes and scores, never a word of the guest or of the draft; the logs of the application carry ids and codes only.</para>
/// <para><b>Tenant.</b> Every method names the organization and every query filters by it; the methods for a person apply the caller's reach (<see cref="HostScope"/>) in SQL. The service
/// checks no role: the web layer decides who may call.</para>
/// </remarks>
public interface IAssistantDraftService
{
    /// <summary>
    /// Processes one incoming message of a guest of <paramref name="orgId"/>, in the order of the brief, each failing step ending in a <see cref="AssistantDecision.NotHandled"/> decision
    /// with its code (<see cref="AssistantDecisionCodes"/>) and no further call to the AI: (a) a property, a writable conversation, the assistant on, an AI provider in effect; (b) a message
    /// recent enough, the latest of its conversation and not answered yet (skipped when a person asked); (c) the classification and the setting of its situation (escalate, a forbidden
    /// topic: a «To do» and no draft); (d) the facts and the draft; (e) the confidence; (f, g) the gate and the queue, with the decision log; (h) the alert of an urgent message.
    /// </summary>
    Task<AssistantDraftOutcome> ProcessInboundAsync(
        Guid orgId,
        MessageThreadKey threadKey,
        Guid inboundMessageId,
        AssistantProcessOptions options,
        CancellationToken ct = default);

    /// <summary>
    /// A person asks for a draft now of the last message of the guest of a conversation in its reach: the age and the «already answered» steps are not looked at, nothing is sent by
    /// itself, and a draft of the conversation that waits in the queue is superseded (rejected with the reason <see cref="AssistantDecisionCodes.RegeneratedReason"/>, which the lessons
    /// ignore) when a new one replaces it. 404 <c>thread_not_found</c> for a conversation out of the reach; 409 <see cref="AssistantDecisionCodes.NoGuestMessageCode"/> when the guest has
    /// written nothing.
    /// </summary>
    Task<AssistantDraftOutcome> DraftNowAsync(HostScope scope, MessageThreadKey threadKey, string userId, CancellationToken ct = default);

    /// <summary>
    /// Runs the classification, the setting of the situation, the facts, the draft and the confidence on an example text, with the current settings of the property and sample values
    /// for the variables of a template, and says what the gate would decide. <b>It writes no log, no queue item and no «To do», and sends nothing</b>; it works while the switch of the
    /// organization is still off, and it spends the AI budget like any call. 422 <see cref="AssistantDecisionCodes.SimulationInvalidCode"/> for a text that is empty or longer than a
    /// message, or a language that is not two letters; 404 <c>property_not_found</c>.
    /// </summary>
    Task<AssistantSimulation> SimulateAsync(Guid orgId, Guid propertyId, string? text, string? language, CancellationToken ct = default);
}

/// <summary>How a message is processed: by itself (<see cref="Automatic"/>), or because a person asked for a draft.</summary>
public sealed record AssistantProcessOptions
{
    /// <summary>A message processed by itself, when it arrives.</summary>
    public static AssistantProcessOptions Automatic { get; } = new();

    /// <summary>A person asked for the draft: the age and the «already answered» steps are not looked at, nothing is sent by itself, the waiting draft is superseded.</summary>
    public bool Requested { get; init; }

    /// <summary>The reach of the person who asked (the draft that is superseded is rejected in it).</summary>
    public HostScope? RequestedBy { get; init; }

    /// <summary>The person who asked.</summary>
    public string? RequestedByUserId { get; init; }
}

/// <summary>
/// What the assistant did with a message. <paramref name="Reason"/> is the code of a decision that was not a draft (why it was not handled, why it was escalated); the score is the
/// confidence of the draft. <paramref name="AlreadyProcessed"/> is true when the message had been processed before and nothing was done again.
/// </summary>
public sealed record AssistantDraftOutcome(
    Guid InboundMessageId,
    AssistantDecision Decision,
    string? Reason,
    AssistantSituation? Situation,
    AssistantUrgency? Urgency,
    decimal? ConfidenceScore,
    Guid? ApprovalItemId,
    Guid? ActivityId,
    bool AlreadyProcessed);

/// <summary>
/// What the simulator answers for an example text: the decision the pipeline would take (a draft, an escalation, or not handled with the <paramref name="Reason"/>), the situation, the
/// urgency, the sensitive categories, the confidence, the draft, the facts it used, and whether the gate would send it (<paramref name="WouldAutoSend"/>, with the failed conditions).
/// </summary>
public sealed record AssistantSimulation(
    AssistantDecision Decision,
    string? Reason,
    AssistantSituation? Situation,
    AssistantUrgency? Urgency,
    SensitiveCategory SensitiveCategories,
    AssistantConfidenceScore? Confidence,
    string? Draft,
    IReadOnlyList<RetrievedFact> FactsUsed,
    bool WouldAutoSend,
    IReadOnlyList<string> GateFailures);

/// <summary>The stable codes of the decisions of the assistant: why a message was not handled or was escalated, and the codes of its API.</summary>
public static class AssistantDecisionCodes
{
    // (a) the conversation and the switches
    public const string ThreadNotFound = "thread_not_found";
    public const string MessageNotFound = "message_not_found";
    public const string PropertyUnassigned = "property_unassigned";
    public const string ThreadClosed = "thread_closed";
    public const string OctorateNotConnected = "octorate_not_connected";
    public const string MappingNotConfirmed = "mapping_not_confirmed";
    public const string AssistantDisabled = "assistant_disabled";
    public const string AiProviderNotConfigured = "ai_provider_not_configured";

    // (b) the message
    public const string MessageTooOld = "message_too_old";
    public const string Superseded = "superseded";
    public const string HostAlreadyReplied = "host_already_replied";

    // the AI
    public const string AiBudgetExceeded = "ai_budget_exceeded";
    public const string AiProviderError = "ai_provider_error";
    public const string AiInvalidAnswer = "ai_invalid_answer";

    // (c) why a message went to a person
    public const string ModeEscalate = "mode_escalate";
    public const string ForbiddenTopic = "forbidden_topic";
    public const string TopicNotAllowed = "topic_not_allowed";

    // (g) a send of the gate that did not go out: the draft waits for a person
    public const string AutoSendFailed = "auto_send_failed";

    /// <summary>The reason a waiting draft is rejected with when a person asks for a new one: the lessons ignore it.</summary>
    public const string RegeneratedReason = "regenerated";

    /// <summary>409 of a draft asked for a conversation where the guest has written nothing.</summary>
    public const string NoGuestMessageCode = "assistant_no_guest_message";

    /// <summary>422 of an example of the simulator that is empty or too long, or of a language that is not a two-letter code.</summary>
    public const string SimulationInvalidCode = "assistant_simulation_invalid";
}
