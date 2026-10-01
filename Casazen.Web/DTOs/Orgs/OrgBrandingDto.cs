using System.ComponentModel.DataAnnotations;
using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// The caller org's public-site branding (BK-12, A3-17): <c>GET/PUT /api/orgs/me/branding</c> and the logo/hero image
/// endpoints. Same values, same names as the anonymous <c>PublicOrgDto</c> / <c>ResolveHostBrandingDto</c>, plus what the
/// console preview needs (slug, display name, "Powered by").
/// </summary>
public class OrgBrandingDto
{
    /// <summary>Absolute public URL of the logo, or null (the site shows the display name).</summary>
    public string? LogoUrl { get; set; }

    /// <summary>Absolute public URL of the hero image, or null (the landing uses the first property photo).</summary>
    public string? HeroImageUrl { get; set; }

    /// <summary><c>#rrggbb</c>, or null for the theme's own color.</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>The theme the public site renders: always one of <see cref="PublicSiteThemes.All"/>.</summary>
    public string PublicThemeId { get; set; } = PublicSiteThemes.Default;

    public string? Tagline { get; set; }

    /// <summary>Public slug of the org (preview link <c>/book/{slug}</c>), edited in the org settings (PL-04).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Name shown on the site when there is no logo, edited in the org settings (PL-04).</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Whether the public site shows "Powered by CasaZen" (effective tier Starter, A3-37).</summary>
    public bool ShowPoweredBy { get; set; }

    public static OrgBrandingDto FromOrg(Org org, PlanTier effectiveTier) => new()
    {
        LogoUrl = org.LogoUrl,
        HeroImageUrl = org.HeroImageUrl,
        PrimaryColor = org.ThemeColor,
        PublicThemeId = PublicSiteThemes.Resolve(org.PublicThemeId),
        Tagline = org.Tagline,
        Slug = org.Slug,
        DisplayName = org.DisplayName,
        ShowPoweredBy = effectiveTier == PlanTier.Starter,
    };
}

/// <summary>
/// Body of <c>PUT /api/orgs/me/branding</c>: the text branding, replaced as a whole (images have their own endpoints).
/// The annotations only bound the input; the rules (hex color, supported theme, tagline length) are
/// <see cref="OrgBrandingRules"/>. Error messages are keys of <c>Resources/SharedResources.resx</c>.
/// </summary>
public class UpdateOrgBrandingDto
{
    /// <summary><c>#rgb</c> or <c>#rrggbb</c>; null or empty for the theme's own color.</summary>
    [MaxLength(20, ErrorMessage = "OrgBrandingColorInvalid")]
    public string? PrimaryColor { get; set; }

    /// <summary>One of <see cref="PublicSiteThemes.All"/>; null or empty for the default theme.</summary>
    [MaxLength(50, ErrorMessage = "OrgBrandingThemeInvalid")]
    public string? PublicThemeId { get; set; }

    /// <summary>Plain text, at most <see cref="OrgBrandingRules.TaglineMaxLength"/> characters once whitespace is collapsed.</summary>
    [MaxLength(500, ErrorMessage = "OrgBrandingTaglineTooLongInput")]
    public string? Tagline { get; set; }
}
