using System.ComponentModel.DataAnnotations;
using Casazen.Web.Seo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The public pages as crawlers and link previews read them (BK-15, A3-20, A8-09, A8-29): the HTML the web app serves to
/// crawlers instead of the empty shell of the single-page app, with <c>title</c>, <c>description</c>, canonical,
/// <c>hreflang</c>, <c>robots</c>, Open Graph, Twitter card and JSON-LD built from the published data. Anonymous, like the
/// pages themselves; the frontend Vercel function <c>api/seo.ts</c> is the only caller, which adds the Host of the request.
/// </summary>
/// <remarks>
/// Deliberately without a rate limiter, like the other crawler endpoints (runbook <c>proxy-ip.md</c>): every request
/// comes from the same few Vercel addresses, so a per-IP bucket would only let one flood cut crawlers off. The reads are
/// indexed lookups of published data. Not found is a real 404 (never a 200 "not found" screen), an old slug or a property
/// reached by id is a 301 to the canonical path on the same host, and a page that must not be indexed says so in its
/// <c>robots</c>. Runbook: <c>docs/runbooks/seo-domain.md</c>.
/// </remarks>
[ApiController]
[Route("api/public/seo")]
[AllowAnonymous]
[Produces("text/html")]
public class PublicSeoController(IPublicSeoService seoService) : ControllerBase
{
    private const string SlugPattern = "^[A-Za-z0-9-]{1,100}$";

    /// <summary>The paths of an org's own host that have a crawler page: <c>/</c> and <c>/property/{slug or id}</c>.</summary>
    private const string HostPathPattern = "^/(?:property/[A-Za-z0-9._~%-]{1,150})?$";

    /// <summary>Header of the 404 for a host that serves no org site (read by the crawler function).</summary>
    public const string UnknownHostHeader = "X-Seo-Host";

    /// <summary>Landing page of an org's booking site.</summary>
    /// <param name="host">Host the crawler used, as the web app saw it: decides whether the page may be indexed there.</param>
    [HttpGet("orgs/{orgSlug}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status301MovedPermanently)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrg(
        [RegularExpression(SlugPattern)] string orgSlug,
        [FromQuery, StringLength(253)] string? host,
        CancellationToken cancellationToken) =>
        ToResult(await seoService.RenderOrgAsync(orgSlug, host, cancellationToken));

    /// <summary>A published property of an org, by slug or id.</summary>
    [HttpGet("orgs/{orgSlug}/properties/{propertySlugOrId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status301MovedPermanently)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProperty(
        [RegularExpression(SlugPattern)] string orgSlug,
        [RegularExpression(SlugPattern)] string propertySlugOrId,
        [FromQuery, StringLength(253)] string? host,
        CancellationToken cancellationToken) =>
        ToResult(await seoService.RenderPropertyAsync(orgSlug, propertySlugOrId, host, cancellationToken));

    /// <summary>
    /// A page of an org's own site by the host the crawler used (BK-16, subdomain or verified custom domain): the landing page
    /// (<c>path=/</c>) or a property (<c>path=/property/{slug or id}</c>). A host that serves no org site answers 404 with
    /// <c>X-Seo-Host: unknown</c>: the web app's own host (the crawler function serves the single-page app there), a preview,
    /// a custom domain waiting for its verification or no longer paid for.
    /// </summary>
    [HttpGet("hosts/page")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status301MovedPermanently)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetHostPage(
        [FromQuery, Required, StringLength(253)] string host,
        [FromQuery, Required, RegularExpression(HostPathPattern)] string path,
        CancellationToken cancellationToken) =>
        ToResult(await seoService.RenderHostPageAsync(host, path, cancellationToken));

    /// <summary>A compliance guide of a comune.</summary>
    [HttpGet("guides/{regionSlug}/{comuneSlug}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetGuide(
        [RegularExpression(SlugPattern)] string regionSlug,
        [RegularExpression(SlugPattern)] string comuneSlug,
        CancellationToken cancellationToken) =>
        ToResult(await seoService.RenderGuideAsync(regionSlug, comuneSlug, cancellationToken));

    /// <summary>The tourist tax page of a comune.</summary>
    [HttpGet("tourist-tax/{comuneSlug}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTouristTax(
        [RegularExpression(SlugPattern)] string comuneSlug,
        CancellationToken cancellationToken) =>
        ToResult(await seoService.RenderTouristTaxAsync(comuneSlug, cancellationToken));

    /// <summary>The hub of the guides.</summary>
    [HttpGet("hub")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHub(CancellationToken cancellationToken) =>
        ToResult(await seoService.RenderHubAsync(cancellationToken));

    private ContentResult ToResult(SeoPageResult result)
    {
        // Never cached by an intermediary: the same URL is also served to people by the single-page app, and an org that
        // unpublishes a property must disappear from crawlers at once.
        Response.Headers.CacheControl = "no-store";

        switch (result.Status)
        {
            case SeoPageStatus.MovedPermanently:
                Response.Headers.Location = result.RedirectPath;
                return Html(StatusCodes.Status301MovedPermanently, result.Html);
            case SeoPageStatus.NotFound:
                return Html(StatusCodes.Status404NotFound, result.Html);
            case SeoPageStatus.UnknownHost:
                Response.Headers[UnknownHostHeader] = "unknown";
                return Html(StatusCodes.Status404NotFound, result.Html);
            default:
                return Html(StatusCodes.Status200OK, result.Html);
        }
    }

    private static ContentResult Html(int status, string html) =>
        new() { StatusCode = status, ContentType = "text/html; charset=utf-8", Content = html };
}
