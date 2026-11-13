using Casazen.Core.Assistant;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Assistant;

/// <summary>
/// What the assistant did with the last message of the guest when a person asked for a draft (<c>POST api/assistant/threads/{threadKey}/draft</c>, E05/F05/US02/T01): the <c>decision</c>
/// (<c>Drafted</c>, <c>Escalated</c>, <c>NotHandled</c>), the <c>reason</c> code of a decision that is not a draft, the situation with its stable <c>situationCode</c>, the urgency, the
/// confidence score, and the item of the approval queue that holds the draft (read it with <c>GET api/approval-queue</c>) or the «To do» of an escalated message. No text.
/// </summary>
public sealed class AssistantDraftOutcomeDto
{
    public Guid InboundMessageId { get; init; }
    public AssistantDecision Decision { get; init; }
    public string? Reason { get; init; }
    public AssistantSituation? Situation { get; init; }
    public string? SituationCode { get; init; }
    public AssistantUrgency? Urgency { get; init; }
    public decimal? ConfidenceScore { get; init; }
    public Guid? ApprovalItemId { get; init; }
    public Guid? ActivityId { get; init; }

    public static AssistantDraftOutcomeDto From(AssistantDraftOutcome outcome) => new()
    {
        InboundMessageId = outcome.InboundMessageId,
        Decision = outcome.Decision,
        Reason = outcome.Reason,
        Situation = outcome.Situation,
        SituationCode = outcome.Situation is { } situation ? AssistantSituationCatalog.Of(situation).Code : null,
        Urgency = outcome.Urgency,
        ConfidenceScore = outcome.ConfidenceScore,
        ApprovalItemId = outcome.ApprovalItemId,
        ActivityId = outcome.ActivityId,
    };
}

/// <summary>
/// The body of <c>POST api/assistant/simulate</c>: the <c>propertyId</c> whose settings are used, the example <c>text</c> of a guest (1 to 2,000 characters) and, optionally, the
/// <c>language</c> the guest writes in (two letters; the classifier's otherwise). No validation attribute: a bad value is a 422 of the service.
/// </summary>
public sealed class SimulateAssistantRequest
{
    public Guid PropertyId { get; set; }
    public string? Text { get; set; }
    public string? Language { get; set; }
}

/// <summary>The confidence of a draft: the score and its three components, 0 to 1.</summary>
public sealed class AssistantConfidenceDto
{
    public decimal Score { get; init; }
    public decimal SituationMatch { get; init; }
    public decimal FactCoverage { get; init; }
    public decimal RuleCompliance { get; init; }
}

/// <summary>A fact the draft of the simulator could use: its topic, its text (the host's own knowledge) and where it comes from (<c>entry:{id}</c> or <c>listing:{field}</c>).</summary>
public sealed class SimulatedFactDto
{
    public KnowledgeTopic Topic { get; init; }
    public string Text { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool Inherited { get; init; }
}

/// <summary>
/// The answer of <c>POST api/assistant/simulate</c> (AC-NF66-4): what the pipeline would decide for the example (<c>decision</c>, with the <c>reason</c> code when it is not a draft), the
/// situation, the urgency, the sensitive categories, the confidence, the draft, the facts it used and whether the gate would send it by itself (<c>wouldAutoSend</c>, with the
/// <c>gateFailures</c>). Nothing was written or sent.
/// </summary>
public sealed class AssistantSimulationDto
{
    public AssistantDecision Decision { get; init; }
    public string? Reason { get; init; }
    public AssistantSituation? Situation { get; init; }
    public string? SituationCode { get; init; }
    public AssistantUrgency? Urgency { get; init; }
    public SensitiveCategory SensitiveCategories { get; init; }
    public AssistantConfidenceDto? Confidence { get; init; }
    public string? Draft { get; init; }
    public IReadOnlyList<SimulatedFactDto> FactsUsed { get; init; } = [];
    public bool WouldAutoSend { get; init; }
    public IReadOnlyList<string> GateFailures { get; init; } = [];

    public static AssistantSimulationDto From(AssistantSimulation simulation) => new()
    {
        Decision = simulation.Decision,
        Reason = simulation.Reason,
        Situation = simulation.Situation,
        SituationCode = simulation.Situation is { } situation ? AssistantSituationCatalog.Of(situation).Code : null,
        Urgency = simulation.Urgency,
        SensitiveCategories = simulation.SensitiveCategories,
        Confidence = simulation.Confidence is { } confidence
            ? new AssistantConfidenceDto
            {
                Score = confidence.Score,
                SituationMatch = confidence.SituationMatch,
                FactCoverage = confidence.FactCoverage,
                RuleCompliance = confidence.RuleCompliance,
            }
            : null,
        Draft = simulation.Draft,
        FactsUsed = simulation.FactsUsed
            .Select(fact => new SimulatedFactDto { Topic = fact.Topic, Text = fact.Text, Source = fact.SourceRef, Inherited = fact.Inherited })
            .ToList(),
        WouldAutoSend = simulation.WouldAutoSend,
        GateFailures = simulation.GateFailures,
    };
}
