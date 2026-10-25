namespace Casazen.Web.DTOs.ApprovalQueue;

/// <summary>
/// Request to reject an approval queue item.
/// </summary>
public class RejectApprovalItemDto
{
    /// <summary>Reason for rejection.</summary>
    public string? Reason { get; set; }
}
