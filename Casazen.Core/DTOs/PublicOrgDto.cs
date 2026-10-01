using Casazen.Core.Branding;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.DTOs;

public class PublicOrgDto
{
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }

    /// <summary>Primary color of the site (<c>#rrggbb</c>), or null for the theme's own color (spec US-023 AC11, BK-12).</summary>
    public string? PrimaryColor { get; set; }

    /// <summary>Same value as <see cref="PrimaryColor"/>, kept for clients written before BK-12.</summary>
    public string? ThemeColor { get; set; }

    /// <summary>
    /// The org's contact email, only when it opted in to publish it (<c>Org.ContactEmailPublic</c>, A1-22/A1-23);
    /// <c>null</c> otherwise — this endpoint is anonymous, so an unset opt-in must never leak the address (GDPR).
    /// </summary>
    public string? ContactEmail { get; set; }

    public string? HeroImageUrl { get; set; }
    public string? Tagline { get; set; }

    /// <summary>The theme the site renders: always one of <c>PublicSiteThemes.All</c> (unset or unsupported → default).</summary>
    public string PublicThemeId { get; set; } = PublicSiteThemes.Default;

    public bool ShowPoweredBy { get; set; }

    /// <summary>
    /// Absolute URL of the landing page on the public domain (<c>App:PublicSiteBaseUrl</c>), for the <c>canonical</c> and
    /// <c>og:url</c> of the page (BK-15); <c>null</c> only when that is not configured (Development/Testing). Set by the
    /// endpoint, never built in the browser.
    /// </summary>
    public string? CanonicalUrl { get; set; }

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
        PrimaryColor = org.ThemeColor,
        ThemeColor = org.ThemeColor,
        ContactEmail = org.ContactEmailPublic && !string.IsNullOrWhiteSpace(org.ContactEmail) ? org.ContactEmail : null,
        HeroImageUrl = org.HeroImageUrl,
        Tagline = org.Tagline,
        PublicThemeId = PublicSiteThemes.Resolve(org.PublicThemeId),
        ShowPoweredBy = effectiveTier == PlanTier.Starter,
    };
}
