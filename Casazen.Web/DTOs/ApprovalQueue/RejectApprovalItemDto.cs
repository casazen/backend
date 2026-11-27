using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.ApprovalQueue;

/// <summary>
/// Request to reject an approval queue item. The reason is mandatory and at most 500 characters: a missing, blank or too long one is a 422
/// <c>approval_rejection_reason_required</c> from the service, not a 400, so the screen has one code to show; the cap here only bounds the body.
/// </summary>
public class RejectApprovalItemDto
{
    /// <summary>Reason for rejection.</summary>
    [MaxLength(5000)]
    public string? Reason { get; set; }
}
