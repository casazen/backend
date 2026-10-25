namespace Casazen.Web.DTOs.ApprovalQueue;

/// <summary>
/// Approval queue item DTO (NF-06).
/// </summary>
public class ApprovalQueueItemDto
{
    public Guid Id { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public Guid ItemId { get; set; }
    public string ContentText { get; set; } = string.Empty;
    public string? ContentVersion { get; set; }
    public bool IsAiGenerated { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
