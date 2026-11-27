using Casazen.Core.Entities;

namespace Casazen.Web.DTOs.ApprovalQueue;

/// <summary>
/// Approval queue item DTO (NF-06, E05/F04, NF-68): what the page of the host and the mobile app need to read, edit, approve or reject an item. Enums by name. The texts are
/// the host's to read (<c>approval.decide</c>, the properties of its reach); the answer is private and never cached.
/// </summary>
public class ApprovalQueueItemDto
{
    public Guid Id { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public Guid ItemId { get; set; }

    /// <summary>The text as it was submitted; an edit never overwrites it.</summary>
    public string ContentText { get; set; } = string.Empty;
    public string? ContentVersion { get; set; }
    public bool IsAiGenerated { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>The property the item concerns; <c>null</c> for an item of the org.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>What approving does: <c>SendGuestMessage</c>, <c>ApproveCampaign</c>, <c>SendReviewReply</c> or <c>None</c> (a pure decision).</summary>
    public string? OriginAction { get; set; }

    /// <summary><c>NotRequired</c>, <c>Pending</c>, <c>Executed</c> or <c>Failed</c>.</summary>
    public string OriginActionStatus { get; set; } = string.Empty;

    /// <summary>The code of the last failure of the action (<c>Failed</c>); <c>null</c> otherwise.</summary>
    public string? OriginActionError { get; set; }

    public DateTime? OriginActionExecutedAt { get; set; }

    /// <summary>
    /// How far the action went, on the answer of an approval only: <c>Sent</c>, <c>Created</c>, <c>WaitingAuthorization</c> or <c>ManualVerify</c> (the outcome of the send is not
    /// known: it is checked on the channel, never sent again) for a message; <c>Scheduled</c> or <c>Approved</c> for a campaign; <c>null</c> for a pure decision and everywhere else.
    /// </summary>
    public string? OriginActionResult { get; set; }

    /// <summary>The score of the assistant for a draft of a message, from 0 to 1; <c>null</c> for every other item.</summary>
    public decimal? ConfidenceScore { get; set; }

    /// <summary>The situation the assistant recognised in the message of the guest.</summary>
    public string? SituationCode { get; set; }

    /// <summary>The conversation of a draft of a message (<c>ota:{id}</c> or <c>direct:{id}</c>): the screen opens it from here.</summary>
    public string? ThreadKey { get; set; }

    /// <summary>The text as the host edited it; <c>null</c> while it was not edited. It is the one the approval acts with.</summary>
    public string? EditedContentText { get; set; }

    public DateTime? EditedAt { get; set; }

    /// <summary>Why the item was rejected.</summary>
    public string? RejectionReason { get; set; }

    /// <summary>The item as the API gives it; <paramref name="actionResult"/> is the state of the action that an approval just ran.</summary>
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
