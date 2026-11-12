using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.ApprovalQueue;

/// <summary>
/// Request to edit the text of an approval queue item (E05/F04/US01/T02). An empty text, a message longer than 2000 characters, or a campaign text that is not a subject line, a blank
/// line and a body is a 422 <c>approval_edit_invalid</c> from the service, so the screen has one code to show; the cap here only bounds the body.
/// </summary>
public class EditApprovalItemDto
{
    /// <summary>The new text. The original stays as it was submitted.</summary>
    [MaxLength(100_000)]
    public string? Text { get; set; }
}
