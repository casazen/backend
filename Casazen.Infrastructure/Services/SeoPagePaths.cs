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

    /// <summary>
    /// The pages of the public site of CasaZen (frontend <c>src/site/site-pages.ts</c>) and the public search of homes: the header
    /// and the footer of the guides link to them, so that a guide is not a page with no way out.
    /// </summary>
    public const string SiteHome = "/";

    public const string SiteFeatures = "/funzionalita";
    public const string SiteSuppliers = "/per-i-fornitori";
    public const string SiteAbout = "/chi-siamo";
    public const string SiteSearch = "/search";

    /// <summary>
    /// The mark of the brand as an image (frontend <c>public/brand</c>): the preview of a guide in a link (<c>og:image</c>) and the
    /// <c>logo</c> of the organization in the structured data.
    /// </summary>
    public const string BrandImage = "/brand/icona-app-1024.png";

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
