using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

/// <summary>
/// F0 spike (#288) — tenant resolution from Host header for edge middleware.
/// Full custom-domain fields land in Fase 1 (US-024).
/// </summary>
public class ResolveHostResponseDto
{
    public Guid OrgId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public PublicHostMode PublicHostMode { get; set; }
    public PublicOrgDto Branding { get; set; } = new();
}
