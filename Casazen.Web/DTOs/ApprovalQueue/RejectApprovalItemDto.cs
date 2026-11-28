using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.ApprovalQueue;

public class RejectApprovalItemDto
{
    [MaxLength(5000)]
    public string? Reason { get; set; }
}
