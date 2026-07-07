using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

public class PublicOrgDto
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? ThemeColor { get; set; }
    public string ContactEmail { get; set; } = string.Empty;
    public string? HeroImageUrl { get; set; }
    public string? Tagline { get; set; }
    public string? PublicThemeId { get; set; }
    public bool ShowPoweredBy { get; set; }

    public static PublicOrgDto FromOrg(Org org) => new()
    {
        Slug = org.Slug,
        DisplayName = org.DisplayName,
        LogoUrl = org.LogoUrl,
        ThemeColor = org.ThemeColor,
        ContactEmail = org.ContactEmail,
        HeroImageUrl = org.HeroImageUrl,
        Tagline = org.Tagline,
        PublicThemeId = org.PublicThemeId,
        ShowPoweredBy = org.PlanTier == PlanTier.Starter,
    };
}
