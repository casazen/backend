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

    public static string For(ComuneInfo comune, SeoPageType pageType) =>
        pageType switch
        {
            SeoPageType.ComplianceGuide => $"{Hub}/{comune.RegionSlug}/{comune.ComuneSlug}",
            SeoPageType.TouristTaxCalc => $"/p/tassa-soggiorno/{comune.ComuneSlug}",
            _ => $"/p/supplier/{comune.ComuneSlug}",
        };
}
