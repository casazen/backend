using Casazen.Core.Branding;
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
    public string PublicThemeId { get; set; } = PublicSiteThemes.Default;

    public string? PublicFontId { get; set; }

    public string? HeroImageUrl { get; set; }
    public string? Tagline { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public bool ShowPoweredBy { get; set; }

    public static ResolveHostBrandingDto FromOrg(Org org, PlanTier effectiveTier) => new()
    {
        LogoUrl = org.LogoUrl,
        PrimaryColor = org.ThemeColor,
        PublicThemeId = PublicSiteThemes.Resolve(org.PublicThemeId),
        PublicFontId = PublicSiteFonts.Resolve(org.PublicFontId),
        HeroImageUrl = org.HeroImageUrl,
        Tagline = org.Tagline,
        DisplayName = org.DisplayName,
        Slug = org.Slug,
        ShowPoweredBy = effectiveTier == PlanTier.Starter,
    };
}
