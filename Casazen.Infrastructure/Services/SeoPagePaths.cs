using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Paths of the public SEO pages in the web app (frontend routes <c>/p/…</c>). The absolute URL is always
/// <c>App:PublicSiteBaseUrl</c> + path (<see cref="Email.PublicSiteLinks"/>): canonical, sitemap and hub use this class
/// only, so they cannot disagree.
/// </summary>
public static class SeoPagePaths
{
    /// <summary>Hub listing every published page (frontend route <c>/p/affitti-brevi</c>).</summary>
    public const string Hub = "/p/affitti-brevi";

    /// <summary>Signup page of the web app (frontend route <c>/signup</c>, SE-03).</summary>
    public const string Signup = "/signup";

    /// <summary><c>utm_source</c> of the CTA of the SEO pages.</summary>
    public const string CtaUtmSource = "seo-compliance";

    /// <summary><c>utm_medium</c> of the CTA of the SEO pages.</summary>
    public const string CtaUtmMedium = "cta";

    /// <summary>
    /// Path and query of the signup CTA of a page: the comune slug (what converts is measured per comune) and the default
    /// UTM parameters, with <c>utm_content</c> telling the page type. The web app replaces the UTM parameters with those of
    /// the visit, when it has some.
    /// </summary>
    public static string SignupCta(ComuneInfo comune, SeoPageType pageType) =>
        $"{Signup}?comune={Uri.EscapeDataString(comune.ComuneSlug)}" +
        $"&utm_source={CtaUtmSource}&utm_medium={CtaUtmMedium}&utm_content={CtaContent(pageType)}";

    private static string CtaContent(SeoPageType pageType) =>
        pageType switch
        {
            SeoPageType.ComplianceGuide => "compliance-guide",
            SeoPageType.TouristTaxCalc => "tourist-tax-calc",
            _ => "supplier-microsite",
        };

    public static string For(ComuneInfo comune, SeoPageType pageType) =>
        pageType switch
        {
            SeoPageType.ComplianceGuide => $"{Hub}/{comune.RegionSlug}/{comune.ComuneSlug}",
            SeoPageType.TouristTaxCalc => $"/p/tassa-soggiorno/{comune.ComuneSlug}",
            _ => $"/p/supplier/{comune.ComuneSlug}",
        };
}
