using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.ApprovalQueue;

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

    public Guid? PropertyId { get; set; }

    public string? OriginAction { get; set; }

    public string OriginActionStatus { get; set; } = string.Empty;

    /// <summary>The code of the last failure of the action (<c>Failed</c>); <c>null</c> otherwise.</summary>
    public string? OriginActionError { get; set; }

    public DateTime? OriginActionExecutedAt { get; set; }

    public string? OriginActionResult { get; set; }

    public decimal? ConfidenceScore { get; set; }

    public string? SituationCode { get; set; }

    public string? ThreadKey { get; set; }

    public string? EditedContentText { get; set; }

    public DateTime? EditedAt { get; set; }

    public string? RejectionReason { get; set; }

    public static ApprovalQueueItemDto From(ApprovalQueueItem item, string? actionResult = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new ApprovalQueueItemDto
        {
            Id = item.Id,
            ItemType = item.ItemType,
            ItemId = item.ItemId,
            ContentText = item.ContentText,
            ContentVersion = item.ContentVersion,
            IsAiGenerated = item.IsAiGenerated,
            Status = item.Status.ToString(),
            ApprovedByUserId = item.ApprovedByUserId,
            ApprovedAt = item.ApprovedAt,
            CreatedAt = item.CreatedAt,
            ExpiresAt = item.ExpiresAt,
            PropertyId = item.PropertyId,
            OriginAction = item.OriginAction,
            OriginActionStatus = item.OriginActionStatus.ToString(),
            OriginActionError = item.OriginActionError,
            OriginActionExecutedAt = item.OriginActionExecutedAt,
            OriginActionResult = actionResult,
            ConfidenceScore = item.ConfidenceScore,
            SituationCode = item.SituationCode,
            ThreadKey = item.ThreadKey,
            EditedContentText = item.EditedContentText,
            EditedAt = item.EditedAt,
            RejectionReason = item.RejectionReason,
        };
    }
}
