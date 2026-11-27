using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Options;

/// <summary>
/// How the assistant of the guests' messages works over time (section <c>Assistant</c>, Railway <c>Assistant__*</c>, E05/F05/US02/T01, NF-67): how old a message may be to wake
/// it, how soon a person must answer an escalated message, and the trust measure the later stories read. Runbook: <c>docs/runbooks/message-assistant.md</c>. A value out of its
/// range is brought within it (nothing fails the startup).
/// </summary>
public sealed class AssistantOptions
{
    public const string SectionName = "Assistant";

    public int MaxMessageAgeHours { get; set; } = 6;

    public AssistantEscalationOptions Escalation { get; set; } = new();

    public AssistantTrustOptions Trust { get; set; } = new();

    public TimeSpan MaxMessageAge() => TimeSpan.FromHours(Math.Clamp(MaxMessageAgeHours, 1, 168));
}

public sealed class AssistantEscalationOptions
{
    public int UrgentHours { get; set; } = 1;

    public int HighHours { get; set; } = 4;

    public int NormalHours { get; set; } = 24;

    public TimeSpan DeadlineAfter(AssistantUrgency urgency) => TimeSpan.FromHours(Math.Clamp(
        urgency switch
        {
            AssistantUrgency.Urgent => UrgentHours,
            AssistantUrgency.High => HighHours,
            _ => NormalHours,
        },
        1,
        168));
}

public sealed class AssistantTrustOptions
{
    public int MinDays { get; set; } = 14;

    public int MinDrafts { get; set; } = 10;

    public int MinApprovedWithoutEditPercent { get; set; } = 90;
}
