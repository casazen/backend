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
    public const string InboundMessageIndexName = "UIX_AssistantDecisionLogs_InboundMessageId";

    public const int ThreadKeyMaxLength = 80;

    public const int ModelMaxLength = 100;

    public const int ApproverUserIdMaxLength = 255;

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    public Guid? PropertyId { get; set; }

    [Required, MaxLength(ThreadKeyMaxLength)]
    public string ThreadKey { get; set; } = string.Empty;

    /// <summary>The message of the guest (an <c>OtaMessage</c> or a <c>GuestMessage</c>); no foreign key, since it can be either.</summary>
    public Guid InboundMessageId { get; set; }

    public AssistantSituation? Situation { get; set; }

    public AssistantUrgency? Urgency { get; set; }

    public SensitiveCategory SensitiveCategories { get; set; }

    [Precision(4, 3)]
    public decimal? ConfidenceScore { get; set; }

    [Precision(4, 3)]
    public decimal? ScoreSituation { get; set; }

    [Precision(4, 3)]
    public decimal? ScoreFacts { get; set; }

    [Precision(4, 3)]
    public decimal? ScoreRules { get; set; }

    public AssistantDecision Decision { get; set; }

    public List<string> GateFailures { get; set; } = [];

    [MaxLength(ModelMaxLength)]
    public string? Model { get; set; }

    [MaxLength(ModelMaxLength)]
    public string? ModelVersion { get; set; }

    public int InstructionVersion { get; set; }

    [Column(TypeName = "jsonb")]
    public string SourceRefs { get; set; } = "[]";

    public Guid? ApprovalItemId { get; set; }

    /// <summary>The message the assistant sent by itself (<c>OtaMessage</c> or <c>GuestMessage</c>); <c>null</c> otherwise. No foreign key.</summary>
    public Guid? OutboundMessageId { get; set; }

    public Guid? ActivityId { get; set; }

    public DateTime? ActivityClosedAt { get; set; }

    [MaxLength(ApproverUserIdMaxLength)]
    public string? ApproverUserId { get; set; }

    public bool? ApprovedWithoutEdit { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
