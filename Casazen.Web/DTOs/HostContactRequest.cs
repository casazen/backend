using System.ComponentModel.DataAnnotations;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs;

/// <summary>
/// Body of «Scrivi a {host}» on the booking site (DB-03b): who writes (an address the host answers to), what, and the consent to send
/// both to the host. In the body, never in the URL. <see cref="Website"/> is the honeypot: a field no person fills.
/// </summary>
public class HostContactRequest
{
    [Required(ErrorMessage = "HostContactEmailInvalid")]
    [MaxLength(HostContactRules.EmailMaxLength, ErrorMessage = "HostContactEmailInvalid")]
    [EmailAddress(ErrorMessage = "HostContactEmailInvalid")]
    public string Email { get; set; } = string.Empty;

    /// <summary>The text, 5 to 2000 characters once trimmed.</summary>
    [Required(ErrorMessage = "HostContactMessageInvalid")]
    [MaxLength(HostContactRules.MessageMaxLength + 200, ErrorMessage = "HostContactMessageInvalid")]
    public string Message { get; set; } = string.Empty;

    [MaxLength(HostContactRules.NameMaxLength, ErrorMessage = "HostContactNameTooLong")]
    public string? Name { get; set; }

    /// <summary>The property the visitor was looking at, when they came from its page; ignored unless it is a published property of the site.</summary>
    public Guid? PropertyId { get; set; }

    /// <summary>The language of the site the visitor was reading, <c>it</c> or <c>en</c>; anything else is ignored.</summary>
    [MaxLength(10)]
    public string? Lang { get; set; }

    /// <summary>The visitor agrees that the message and their address are sent to the host. Must be <c>true</c>.</summary>
    public bool Consent { get; set; }

    /// <summary>Honeypot: left empty by every person. Filled, the message is dropped and the answer is the same as for a real one.</summary>
    [MaxLength(500)]
    public string? Website { get; set; }
}
