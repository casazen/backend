using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// What the assistant did with one incoming message of a guest (E05/F05/US01/T01, NF-67, PO-09): the situation and the urgency it recognized, the score and its
/// three components, the decision, the model and the version of the instructions that wrote the draft, the sources it used and who approved it. <b>One row per
/// message</b> (unique index on <see cref="InboundMessageId"/>): processing a message twice finds the first row.
/// </summary>
/// <remarks>
/// <b>Ids and codes only, never text.</b> The log holds no word of the guest's message and none of the draft (PO-09 minimization): the message is named by its
/// id, the conversation by its key, the sources by the ids of the entries of the knowledge base and the topics. The reason a message was not handled, and the
/// conditions of the gate that failed, are stable codes in <see cref="GateFailures"/>. The row survives the item of the approval queue it points to
/// (<see cref="ApprovalItemId"/> is set to <c>null</c> if the item goes).
/// </remarks>
[Table("AssistantDecisionLogs")]
public class AssistantDecisionLog : ITenantOwned
{
    /// <summary>Name of the unique index on <see cref="InboundMessageId"/>.</summary>
    public const string InboundMessageIndexName = "UIX_AssistantDecisionLogs_InboundMessageId";

    public const int ThreadKeyMaxLength = 80;

    public const int ModelMaxLength = 100;

    public const int ApproverUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant of the row. Never client-supplied.</summary>
    public Guid OrgId { get; set; }

    /// <summary>The property of the conversation; <c>null</c> for a conversation that has none (the message was not handled: <c>property_unassigned</c>).</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>The conversation: <c>ota:{guid}</c> or <c>direct:{guid}</c> (<c>MessageThreadKey</c>).</summary>
    [Required, MaxLength(ThreadKeyMaxLength)]
    public string ThreadKey { get; set; } = string.Empty;

    /// <summary>The message of the guest (an <c>OtaMessage</c> or a <c>GuestMessage</c>); no foreign key, since it can be either.</summary>
    public Guid InboundMessageId { get; set; }

    /// <summary>The situation recognized; <c>null</c> when the message was not classified.</summary>
    public AssistantSituation? Situation { get; set; }

    public AssistantUrgency? Urgency { get; set; }

    public SensitiveCategory SensitiveCategories { get; set; }

    /// <summary>The confidence score, 0 to 1 (the product of the three components below); <c>null</c> when no draft was written.</summary>
    [Precision(4, 3)]
    public decimal? ConfidenceScore { get; set; }

    /// <summary>How well the message matched the situation, 0 to 1.</summary>
    [Precision(4, 3)]
    public decimal? ScoreSituation { get; set; }

    /// <summary>How much of what the answer needs the knowledge base had, 0 to 1.</summary>
    [Precision(4, 3)]
    public decimal? ScoreFacts { get; set; }

    /// <summary>How well the draft followed the rules, 0 to 1.</summary>
    [Precision(4, 3)]
    public decimal? ScoreRules { get; set; }

    public AssistantDecision Decision { get; set; }

    /// <summary>Stable codes: the conditions of the auto-send gate that failed, or why the message was not handled. Stored as <c>text[]</c>.</summary>
    public List<string> GateFailures { get; set; } = [];

    /// <summary>The model that wrote the draft (<c>AiOptions.Model</c>); <c>null</c> when there was no call.</summary>
    [MaxLength(ModelMaxLength)]
    public string? Model { get; set; }

    [MaxLength(ModelMaxLength)]
    public string? ModelVersion { get; set; }

    /// <summary>The <see cref="OrgAssistantRules.InstructionVersion"/> the draft was written with.</summary>
    public int InstructionVersion { get; set; }

    /// <summary>The sources of the draft as JSON: the ids of the entries of the knowledge base and the topics of the facts built from the listing. No text.</summary>
    [Column(TypeName = "jsonb")]
    public string SourceRefs { get; set; } = "[]";

    /// <summary>The draft in the approval queue (foreign key, SET NULL); <c>null</c> when none was queued.</summary>
    public Guid? ApprovalItemId { get; set; }

    /// <summary>The message the assistant sent by itself (<c>OtaMessage</c> or <c>GuestMessage</c>); <c>null</c> otherwise. No foreign key.</summary>
    public Guid? OutboundMessageId { get; set; }

    /// <summary>The «To do» raised for an escalated message; <c>null</c> when none.</summary>
    public Guid? ActivityId { get; set; }

    /// <summary>When the «To do» was closed (a person answered); <c>null</c> while it is open.</summary>
    public DateTime? ActivityClosedAt { get; set; }

    /// <summary>The user who approved or rejected the draft (<c>User.Id</c>), an opaque id with no foreign key.</summary>
    [MaxLength(ApproverUserIdMaxLength)]
    public string? ApproverUserId { get; set; }

    /// <summary>The draft was approved exactly as written (the measure of the trust level, NF-67); <c>null</c> until a person decides.</summary>
    public bool? ApprovedWithoutEdit { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
