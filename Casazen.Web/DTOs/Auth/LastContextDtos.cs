using System.ComponentModel.DataAnnotations;

namespace Casazen.Web.DTOs.Auth;

/// <summary>Body of <c>PUT /api/me/last-context</c> (UI-13a): the area the caller entered.</summary>
public sealed class SetLastContextRequest
{
    /// <summary>A <c>contextKey</c> of <c>GET /api/me/contexts</c> (<c>short-rent</c>, <c>long-rent</c>, <c>supplier</c>, <c>admin</c>, <c>account</c>).</summary>
    [Required(ErrorMessage = "ContextKeyRequired")]
    [MaxLength(64, ErrorMessage = "ContextKeyRequired")]
    public string? ContextKey { get; set; }
}

/// <summary>The answer of <c>PUT /api/me/last-context</c>: the key as stored, the one <c>GET /api/me/contexts</c> gives back as <c>lastUsedContextKey</c>.</summary>
public sealed record LastContextResponse(string LastUsedContextKey);
