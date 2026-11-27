using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

public class HostContactRequest
{
    [Required(ErrorMessage = "HostContactEmailInvalid")]
    [MaxLength(HostContactRules.EmailMaxLength, ErrorMessage = "HostContactEmailInvalid")]
    [EmailAddress(ErrorMessage = "HostContactEmailInvalid")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "HostContactMessageInvalid")]
    [MaxLength(HostContactRules.MessageMaxLength + 200, ErrorMessage = "HostContactMessageInvalid")]
    public string Message { get; set; } = string.Empty;

    [MaxLength(HostContactRules.NameMaxLength, ErrorMessage = "HostContactNameTooLong")]
    public string? Name { get; set; }

    /// <summary>The property the visitor was looking at, when they came from its page; ignored unless it is a published property of the site.</summary>
    public Guid? PropertyId { get; set; }

    [MaxLength(10)]
    public string? Lang { get; set; }

    public bool Consent { get; set; }

    [MaxLength(500)]
    public string? Website { get; set; }
}
