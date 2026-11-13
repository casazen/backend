using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Core.Entities;

/// <summary>
/// Queue item requiring human approval before taking effect (NF-06).
/// Covers campaign drafts, AI-generated texts, routine messages in test mode and, since E05/F04 (NF-68), the drafts of the assistant for a conversation
/// of the hub of messages. Approving an item runs the action it was waiting for (<see cref="OriginAction"/>, <see cref="OriginActionStatus"/>); the host
/// may edit the text first (<see cref="EditedContentText"/>, the original <see cref="ContentText"/> is never overwritten); a rejection keeps its reason.
/// </summary>
/// <remarks>
/// <see cref="ContentText"/> and <see cref="EditedContentText"/> are the words of a message to a guest or of a mailing: they are encrypted at rest with the
/// purpose of the words of the conversations (<c>EncryptedColumns</c>, ADR-032-R3), never searched and never logged.
/// </remarks>
[Table("ApprovalQueueItems")]
public class ApprovalQueueItem : ITenantOwned
{
    /// <summary>Longest <see cref="RejectionReason"/>.</summary>
    public const int RejectionReasonMaxLength = 500;

    /// <summary>Longest <see cref="OriginAction"/>.</summary>
    public const int OriginActionMaxLength = 50;

    /// <summary>Longest <see cref="OriginActionError"/>: a code, never a sentence.</summary>
    public const int OriginActionErrorMaxLength = 100;

    /// <summary>Longest <see cref="SituationCode"/>, the same as the rule code of a message of the assistant (<see cref="OtaMessage.RuleCodeMaxLength"/>).</summary>
    public const int SituationCodeMaxLength = 64;

    /// <summary>Longest <see cref="ThreadKey"/>: a key of the hub of messages is at most 43 characters.</summary>
    public const int ThreadKeyMaxLength = 80;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org that owns the item requiring approval.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Type of item: Campaign, MessageDraft, Routine, AiText.</summary>
    [Required, MaxLength(50)]
    public string ItemType { get; set; } = string.Empty;

    /// <summary>ID of the item requiring approval.</summary>
    public Guid ItemId { get; set; }

    /// <summary>The exact text content submitted for approval. Never overwritten by an edit (<see cref="EditedContentText"/>).</summary>
    [Required]
    public string ContentText { get; set; } = string.Empty;

    /// <summary>Version identifier of the content (e.g., content hash or revision number).</summary>
    [MaxLength(100)]
    public string? ContentVersion { get; set; }

    /// <summary>Whether the content was AI-generated.</summary>
    public bool IsAiGenerated { get; set; }

    /// <summary>Current status: Pending, Approved, Rejected, Expired.</summary>
    public ApprovalStatus Status { get; set; }

    /// <summary>User who approved or rejected the item (Auth0 subject).</summary>
    [MaxLength(255)]
    public string? ApprovedByUserId { get; set; }

    /// <summary>UTC instant the item was approved or rejected.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>UTC instant the item was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>UTC instant when the item expires (7 days from creation).</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// The property the item concerns (E05/F04/US01/T01), which decides who reaches it: a caller whose reach is limited to some properties sees and
    /// decides only the items of those. <c>null</c> for an item of the org (a campaign) or of a conversation with no property: only a caller who reaches
    /// every property sees it. Not a foreign key, like <see cref="OrgId"/>.
    /// </summary>
    public Guid? PropertyId { get; set; }

    /// <summary>Why the item was rejected, written by the person who rejected it; mandatory for a rejection (at most <see cref="RejectionReasonMaxLength"/> characters).</summary>
    [MaxLength(RejectionReasonMaxLength)]
    public string? RejectionReason { get; set; }

    /// <summary>The text as the host edited it before deciding; <c>null</c> while it was not edited. When it is set, it is the text the action runs with.</summary>
    public string? EditedContentText { get; set; }

    /// <summary>The user who last edited the text (Auth0 subject).</summary>
    [MaxLength(255)]
    public string? EditedByUserId { get; set; }

    /// <summary>UTC instant of the last edit.</summary>
    public DateTime? EditedAt { get; set; }

    /// <summary>
    /// What approving the item does (<see cref="Services.ApprovalOriginActions"/>): <c>SendGuestMessage</c>, <c>ApproveCampaign</c>, <c>SendReviewReply</c>, or
    /// <c>None</c> for a pure decision. <c>null</c> for an item of before the column existed.
    /// </summary>
    [MaxLength(OriginActionMaxLength)]
    public string? OriginAction { get; set; }

    /// <summary>Where the action stands. <see cref="ApprovalOriginActionStatus.NotRequired"/> for an item with no action and for every item of before the column existed.</summary>
    public ApprovalOriginActionStatus OriginActionStatus { get; set; }

    /// <summary>The code of the last failure of the action (<see cref="ApprovalOriginActionStatus.Failed"/>); <c>null</c> otherwise. A code, never a message.</summary>
    [MaxLength(OriginActionErrorMaxLength)]
    public string? OriginActionError { get; set; }

    /// <summary>UTC instant the action ran (<see cref="ApprovalOriginActionStatus.Executed"/>).</summary>
    public DateTime? OriginActionExecutedAt { get; set; }

    /// <summary>The score of the assistant for a draft of a message, from 0 to 1 (F05); <c>null</c> for every other item.</summary>
    [Precision(4, 3)]
    public decimal? ConfidenceScore { get; set; }

    /// <summary>The situation the assistant recognised in the message of the guest (F05); also the rule code of the message once it is sent unedited.</summary>
    [MaxLength(SituationCodeMaxLength)]
    public string? SituationCode { get; set; }

    /// <summary>The conversation a draft of a message belongs to: <c>ota:{guid}</c> or <c>direct:{guid}</c> (<c>MessageThreadKey</c>); <c>null</c> for every other item.</summary>
    [MaxLength(ThreadKeyMaxLength)]
    public string? ThreadKey { get; set; }

    /// <summary>The "To do" raised for the item (ADR-026), closed when the item is decided or expires. <c>null</c> until it is raised.</summary>
    public Guid? ActivityId { get; set; }
}
