using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

/// <summary>
/// Slim public branding projection for <c>GET /api/public/resolve-host</c> (#298). Deliberately
/// excludes <c>ContactEmail</c> and any other PII that <see cref="PublicOrgDto"/> exposes on the
/// authenticated public org endpoints — resolve-host is anonymous edge-middleware traffic.
/// </summary>
public class ResolveHostBrandingDto
{
    public string? LogoUrl { get; set; }
    public string? PrimaryColor { get; set; }
    public string? PublicThemeId { get; set; }
    public string? HeroImageUrl { get; set; }
    public string? Tagline { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool ShowPoweredBy { get; set; }

    public static ResolveHostBrandingDto FromOrg(Org org) => new()
    {
        LogoUrl = org.LogoUrl,
        PrimaryColor = org.ThemeColor,
        PublicThemeId = org.PublicThemeId,
        HeroImageUrl = org.HeroImageUrl,
        Tagline = org.Tagline,
        DisplayName = org.DisplayName,
        Slug = org.Slug,
        ShowPoweredBy = org.PlanTier == PlanTier.Starter,
    };
}
