using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Whether a draft of the assistant may be sent to the guest by itself (E05/F05/US02/T01 for the contract, E05/F05/US03 for the real gate, NF-67, PO-08). <b>The default registration
/// never allows it</b> (<c>NeverAutoSendGate</c>, code <see cref="AutoSendVerdict.NotEnabledCode"/>): nothing is sent by itself until the real gate replaces it, and every draft goes to the
/// approval queue. The gate decides only; the pipeline sends.
/// </summary>
public interface IAutoSendGate
{
    /// <summary>The verdict on one draft: allowed, or the codes of the conditions that failed (stable codes, recorded in the decision log).</summary>
    Task<AutoSendVerdict> EvaluateAsync(AutoSendContext context, CancellationToken ct);
}

/// <summary>
/// What the gate judges a draft by: the organization and the property, the conversation, the situation with its effective mode and the start of its trust period (<c>null</c> is an
/// unknown start, never an elapsed period: <see cref="AssistantModeRules.TrustPeriodElapsed"/>), the urgency, the sensitive categories of the message and of the situation, the intents,
/// the confidence, the rules in force (threshold, hours) and the instant. <paramref name="Simulation"/> is true when the simulator asks: nothing will be sent either way.
/// </summary>
public sealed record AutoSendContext(
    Guid OrgId,
    Guid PropertyId,
    string? ThreadKey,
    AssistantSituation Situation,
    AssistantMode Mode,
    DateTime? ModeSince,
    AssistantUrgency Urgency,
    SensitiveCategory SensitiveCategories,
    IReadOnlyList<string> IntentFlags,
    AssistantConfidenceScore Confidence,
    EffectiveAssistantRules Rules,
    bool FromTemplate,
    bool Simulation,
    DateTime AtUtc);

/// <summary>The verdict of the gate: <paramref name="Allowed"/>, or the codes of the conditions that failed.</summary>
public sealed record AutoSendVerdict(bool Allowed, IReadOnlyList<string> FailureCodes)
{
    /// <summary>No real gate is registered: nothing is sent by itself.</summary>
    public const string NotEnabledCode = "auto_send_not_enabled";

    /// <summary>A refusal with its codes.</summary>
    public static AutoSendVerdict Refused(params string[] codes) => new(false, codes);
}
