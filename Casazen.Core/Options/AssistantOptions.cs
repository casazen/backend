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

    /// <summary>
    /// The oldest a message of a guest may be, in hours, for the assistant to look at it by itself (default 6; 1 to 168): the history copied when a channel is connected, and a
    /// message that waited in a queue for longer, never wake it. A person can still ask for a draft of any message.
    /// </summary>
    public int MaxMessageAgeHours { get; set; } = 6;

    /// <summary>The deadlines of the «To do» of an escalated message, by urgency.</summary>
    public AssistantEscalationOptions Escalation { get; set; } = new();

    /// <summary>When a situation has earned the trust to be answered by itself (read by the trust measure of E05/F05/US03, not by the drafts).</summary>
    public AssistantTrustOptions Trust { get; set; } = new();

    /// <summary><see cref="MaxMessageAgeHours"/> within its range.</summary>
    public TimeSpan MaxMessageAge() => TimeSpan.FromHours(Math.Clamp(MaxMessageAgeHours, 1, 168));
}

/// <summary>How soon a person must answer a message the assistant passed on (the deadline of its «To do»), in hours from the moment it was escalated.</summary>
public sealed class AssistantEscalationOptions
{
    /// <summary>An urgent message (default 1 hour).</summary>
    public int UrgentHours { get; set; } = 1;

    /// <summary>A message of high urgency (default 4 hours).</summary>
    public int HighHours { get; set; } = 4;

    /// <summary>Any other message (default 24 hours).</summary>
    public int NormalHours { get; set; } = 24;

    /// <summary>The time a person has to answer a message of <paramref name="urgency"/>: the hours of its urgency, 1 to 168.</summary>
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

/// <summary>
/// The conditions of PO-08 under which a situation may be proposed for the automatic modes (read by E05/F05/US03): a trust period of <see cref="MinDays"/> days in its mode, at
/// least <see cref="MinDrafts"/> drafts in it, and at least <see cref="MinApprovedWithoutEditPercent"/> per cent of them approved as they were written.
/// </summary>
public sealed class AssistantTrustOptions
{
    /// <summary>Days a situation has to stay in its mode (default 14).</summary>
    public int MinDays { get; set; } = 14;

    /// <summary>Drafts a situation needs in that time (default 10).</summary>
    public int MinDrafts { get; set; } = 10;

    /// <summary>Per cent of those drafts approved with no edit (default 90).</summary>
    public int MinApprovedWithoutEditPercent { get; set; } = 90;
}
