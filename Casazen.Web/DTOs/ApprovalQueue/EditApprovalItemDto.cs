using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.ApprovalQueue;

public class EditApprovalItemDto
{
    [MaxLength(100_000)]
    public string? Text { get; set; }
}
