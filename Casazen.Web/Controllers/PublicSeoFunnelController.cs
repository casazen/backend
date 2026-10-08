using System.Text.RegularExpressions;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.DTOs.Seo;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// The funnel of the public SEO pages (SE-04, #300 AC2 and AC3): the properties of a comune a visitor can book, and the
/// events that tell which comuni bring signups. Anonymous, like the pages: no token, no cookie, no personal data. The
/// text, the meta and the CTA of the pages are the read APIs of <see cref="PublicContentController"/> (AC1). The HTML that
/// crawlers read under the same prefix is <see cref="PublicSeoController"/> (BK-15).
/// </summary>
[ApiController]
[Route("api/public/seo")]
[AllowAnonymous]
public partial class PublicSeoFunnelController(
    ISeoEventService seoEventService,
    ISeoFeaturedPropertiesService featuredPropertiesService) : ControllerBase
{
    /// <summary>
    /// Records one event of the funnel (<c>cta_click</c>, <c>signup_start</c>). Rate limited per client IP; the IP is kept
    /// only in the limiter's memory, never in the event. 204 even for a repeated click: nothing is deduplicated.
    /// </summary>
    [HttpPost("events")]
    [EnableRateLimiting(RateLimitPolicies.PublicSeoEvents)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RecordEvent([FromBody] SeoEventRequestDto request, CancellationToken cancellationToken)
    {
        await seoEventService.RecordAsync(request.ToInput(), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Published properties of a comune (slug or ISTAT code) with what a card and the link to the host's booking site
    /// need (<c>/book/{orgSlug}/property/{slug}</c>): only what the public booking site shows (active, not paused,
    /// compliance activated, org active), at most <see cref="ISeoFeaturedPropertiesService.MaxProperties"/>. 404 for a
    /// comune CasaZen does not know; an empty list for a comune with no published property.
    /// </summary>
    [HttpGet("{comune}/featured-properties")]
    [EnableRateLimiting(RateLimitPolicies.PublicRead)]
    [ProducesResponseType(typeof(SeoFeaturedPropertiesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoFeaturedPropertiesDto>> GetFeaturedProperties(
        string comune,
        CancellationToken cancellationToken)
    {
        if (comune.Length > SignupAttributionRules.MaxComuneLength || !ComuneRoute().IsMatch(comune))
            return this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail");

        var result = await featuredPropertiesService.GetAsync(comune, cancellationToken);
        return result is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail")
            : Ok(result);
    }

    [GeneratedRegex(SignupAttributionRules.ComunePattern)]
    private static partial Regex ComuneRoute();
}
