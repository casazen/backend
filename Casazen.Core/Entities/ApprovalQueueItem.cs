using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;

namespace Casazen.Core.Entities;

/// <summary>
/// Queue item requiring human approval before taking effect (NF-06).
/// Covers campaign drafts, AI-generated texts, routine messages in test mode.
/// </summary>
[Table("ApprovalQueueItems")]
public class ApprovalQueueItem : ITenantOwned
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Tenant: the org that owns the item requiring approval.</summary>
    public Guid OrgId { get; set; }

    /// <summary>Type of item: Campaign, Routine, AiText.</summary>
    [Required, MaxLength(50)]
    public string ItemType { get; set; } = string.Empty;

    /// <summary>ID of the item requiring approval.</summary>
    public Guid ItemId { get; set; }

    /// <summary>The exact text content submitted for approval.</summary>
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
}
