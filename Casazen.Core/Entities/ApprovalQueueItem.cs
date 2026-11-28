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
    public const int RejectionReasonMaxLength = 500;

    public const int OriginActionMaxLength = 50;

    public const int OriginActionErrorMaxLength = 100;

    public const int SituationCodeMaxLength = 64;

    public const int ThreadKeyMaxLength = 80;

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OrgId { get; set; }

    [Required, MaxLength(50)]
    public string ItemType { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    [Required]
    public string ContentText { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? ContentVersion { get; set; }

    public bool IsAiGenerated { get; set; }

    public ApprovalStatus Status { get; set; }

    [MaxLength(255)]
    public string? ApprovedByUserId { get; set; }

    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public Guid? PropertyId { get; set; }

    [MaxLength(RejectionReasonMaxLength)]
    public string? RejectionReason { get; set; }

    public string? EditedContentText { get; set; }

    [MaxLength(255)]
    public string? EditedByUserId { get; set; }

    public DateTime? EditedAt { get; set; }

    [MaxLength(OriginActionMaxLength)]
    public string? OriginAction { get; set; }

    public ApprovalOriginActionStatus OriginActionStatus { get; set; }

    /// <summary>The code of the last failure of the action (<see cref="ApprovalOriginActionStatus.Failed"/>); <c>null</c> otherwise. A code, never a message.</summary>
    [MaxLength(OriginActionErrorMaxLength)]
    public string? OriginActionError { get; set; }

    public DateTime? OriginActionExecutedAt { get; set; }

    [Precision(4, 3)]
    public decimal? ConfidenceScore { get; set; }

    [MaxLength(SituationCodeMaxLength)]
    public string? SituationCode { get; set; }

    [MaxLength(ThreadKeyMaxLength)]
    public string? ThreadKey { get; set; }

    public Guid? ActivityId { get; set; }
}
