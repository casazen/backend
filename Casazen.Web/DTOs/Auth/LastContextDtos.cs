using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Auth;

public sealed class SetLastContextRequest
{
    [Required(ErrorMessage = "ContextKeyRequired")]
    [MaxLength(64, ErrorMessage = "ContextKeyRequired")]
    public string? ContextKey { get; set; }
}

public sealed record LastContextResponse(string LastUsedContextKey);
