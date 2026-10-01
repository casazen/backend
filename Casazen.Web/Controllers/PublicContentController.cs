using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public SEO compliance pages and tourist tax calculator (US-020 #258).
/// AllowAnonymous: marketing/SEO pages must be indexable without auth; no PII collected (AC2, AC3, AC8).
/// A page is public only with a revision approved by an admin, and it shows that revision, never a newer draft, in every
/// environment (SE-01, A8-04, A8-05): drafts are read in the admin SEO dashboard.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicContentController(ISeoContentService seoContentService) : ControllerBase
{

    /// <summary>
    /// Hub of the published pages (SE-02, A8-02): the same pages as the sitemap, linked from the public footer so that
    /// crawlers and visitors can reach every <c>/p/*</c> page from the web app.
    /// </summary>
    [HttpGet("content")]
    [ProducesResponseType(typeof(SeoPublishedPagesDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<SeoPublishedPagesDto>> GetPublishedPages(CancellationToken cancellationToken) =>
        Ok(await seoContentService.GetPublishedPagesAsync(cancellationToken));

    [HttpGet("content/affitti-brevi/{regionSlug}/{comuneSlug}")]
    [ProducesResponseType(typeof(SeoPagePublicDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoPagePublicDto>> GetComplianceGuide(
        string regionSlug,
        string comuneSlug,
        CancellationToken cancellationToken)
    {
        var page = await seoContentService.GetComplianceGuideAsync(regionSlug, comuneSlug, cancellationToken);

        return page is null ? NotFound() : Ok(page);
    }

    [HttpGet("content/tassa-soggiorno/{comuneSlug}")]
    [ProducesResponseType(typeof(SeoPagePublicDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoPagePublicDto>> GetTouristTaxPage(
        string comuneSlug,
        CancellationToken cancellationToken)
    {
        var page = await seoContentService.GetTouristTaxPageAsync(comuneSlug, cancellationToken);

        return page is null ? NotFound() : Ok(page);
    }

    /// <summary>
    /// Public tourist tax calculator of a comune page (A8-12, A8-23), with the engine of the checkout (BK-03). A comune
    /// without rate answers 200 with <c>status = RateUnavailable</c>, never an error; 404 only for an unknown comune.
    /// </summary>
    [HttpPost("tourist-tax/calculate")]
    [EnableRateLimiting(RateLimitPolicies.PublicTouristTaxCalc)]
    [ProducesResponseType(typeof(PublicTouristTaxCalculateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PublicTouristTaxCalculateResponse>> CalculateTouristTax(
        [FromBody] PublicTouristTaxCalculateRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidCalculation(request))
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                TouristTaxQuoteInvalidCode,
                "TouristTaxQuoteInvalid");
        }

        var result = await seoContentService.CalculateTouristTaxAsync(request, cancellationToken);
        return result is null
            ? this.ApiProblem(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "NotFoundDetail")
            : Ok(result);
    }

    /// <summary>Problem code of an invalid calculator request (400).</summary>
    public const string TouristTaxQuoteInvalidCode = "tourist_tax_quote_invalid";

    private static bool IsValidCalculation(PublicTouristTaxCalculateRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ComuneSlug))
            return false;

        // Same calendar dates as the engine (Europe/Rome), so a valid request never reaches it with 0 nights.
        var nights = RomeCalendar.DateInRome(request.CheckOutDate).DayNumber
            - RomeCalendar.DateInRome(request.CheckInDate).DayNumber;
        var ages = request.ChildrenAges;
        return request.NumberOfAdults is >= 1 and <= 100
            && request.NumberOfChildren is >= 0 and <= 100
            && nights is >= 1 and <= TouristTaxCalculator.MaxNights
            && (ages is null
                || (ages.Count == request.NumberOfChildren
                    && ages.All(age => age is >= 0 and < TouristTaxCalculator.AdultAge)))
            && request.NightlyPrice is null or (>= 0m and <= 1_000_000m)
            && (request.AccommodationCategory is null || request.AccommodationCategory.Length <= 100);
    }
}
