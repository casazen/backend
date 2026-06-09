namespace Casazen.Core.DTOs;

/// <summary>
/// Public branding read-model for anonymous branded booking sites (US-003 AC1).
/// </summary>
public class PublicOrgDto
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? ThemeColor { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
}
