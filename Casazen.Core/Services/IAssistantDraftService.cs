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
    Task<AssistantDraftOutcome> ProcessInboundAsync(
        Guid orgId,
        MessageThreadKey threadKey,
        Guid inboundMessageId,
        AssistantProcessOptions options,
        CancellationToken ct = default);

    Task<AssistantDraftOutcome> DraftNowAsync(HostScope scope, MessageThreadKey threadKey, string userId, CancellationToken ct = default);

    Task<AssistantSimulation> SimulateAsync(Guid orgId, Guid propertyId, string? text, string? language, CancellationToken ct = default);
}

/// <summary>How a message is processed: by itself (<see cref="Automatic"/>), or because a person asked for a draft.</summary>
public sealed record AssistantProcessOptions
{
    public static AssistantProcessOptions Automatic { get; } = new();

    public bool Requested { get; init; }

    public HostScope? RequestedBy { get; init; }

    public string? RequestedByUserId { get; init; }
}

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

public static class AssistantDecisionCodes
{
    public const string ThreadNotFound = "thread_not_found";
    public const string MessageNotFound = "message_not_found";
    public const string PropertyUnassigned = "property_unassigned";
    public const string ThreadClosed = "thread_closed";
    public const string OctorateNotConnected = "octorate_not_connected";
    public const string MappingNotConfirmed = "mapping_not_confirmed";
    public const string AssistantDisabled = "assistant_disabled";
    public const string AiProviderNotConfigured = "ai_provider_not_configured";

    public const string MessageTooOld = "message_too_old";
    public const string Superseded = "superseded";
    public const string HostAlreadyReplied = "host_already_replied";

    public const string AiBudgetExceeded = "ai_budget_exceeded";
    public const string AiProviderError = "ai_provider_error";
    public const string AiInvalidAnswer = "ai_invalid_answer";

    public const string ModeEscalate = "mode_escalate";
    public const string ForbiddenTopic = "forbidden_topic";
    public const string TopicNotAllowed = "topic_not_allowed";

    public const string AutoSendFailed = "auto_send_failed";

    public const string RegeneratedReason = "regenerated";

    public const string NoGuestMessageCode = "assistant_no_guest_message";

    public const string SimulationInvalidCode = "assistant_simulation_invalid";
}
