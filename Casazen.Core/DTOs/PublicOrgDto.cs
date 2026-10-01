using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

public class PublicOrgDto
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? ThemeColor { get; set; }

    /// <summary>
    /// The org's contact email, only when it opted in to publish it (<c>Org.ContactEmailPublic</c>, A1-22/A1-23);
    /// <c>null</c> otherwise — this endpoint is anonymous, so an unset opt-in must never leak the address (GDPR).
    /// </summary>
    public string? ContactEmail { get; set; }

    public string? HeroImageUrl { get; set; }
    public string? Tagline { get; set; }
    public string? PublicThemeId { get; set; }
    public bool ShowPoweredBy { get; set; }

    /// <param name="org">The public org.</param>
    /// <param name="effectiveTier">
    /// The org's effective tier (<c>IEntitlementService.ResolveEffectiveTier</c>), never the stored one: a canceled
    /// or unpaid plan must show "Powered by" again (A3-37).
    /// </param>
    public static PublicOrgDto FromOrg(Org org, PlanTier effectiveTier) => new()
    {
        Slug = org.Slug,
        DisplayName = org.DisplayName,
        LogoUrl = org.LogoUrl,
        ThemeColor = org.ThemeColor,
        ContactEmail = org.ContactEmailPublic && !string.IsNullOrWhiteSpace(org.ContactEmail) ? org.ContactEmail : null,
        HeroImageUrl = org.HeroImageUrl,
        Tagline = org.Tagline,
        PublicThemeId = org.PublicThemeId,
        ShowPoweredBy = effectiveTier == PlanTier.Starter,
    };
}
