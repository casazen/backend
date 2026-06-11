using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

/// <summary>
/// Public SEO compliance pages and tourist tax calculator (US-020 #258).
/// AllowAnonymous: marketing/SEO pages must be indexable without auth; no PII collected (AC2, AC3, AC8).
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicContentController(
    ISeoContentService seoContentService,
    IWebHostEnvironment environment) : ControllerBase
{
    private bool AllowDraftPages() =>
        environment.IsStaging() || environment.IsDevelopment() || environment.EnvironmentName == "Testing";

    [HttpGet("content/affitti-brevi/{regionSlug}/{comuneSlug}")]
    [ProducesResponseType(typeof(SeoPagePublicDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoPagePublicDto>> GetComplianceGuide(
        string regionSlug,
        string comuneSlug,
        CancellationToken cancellationToken)
    {
        var page = await seoContentService.GetComplianceGuideAsync(
            regionSlug,
            comuneSlug,
            AllowDraftPages(),
            cancellationToken);

        return page is null ? NotFound() : Ok(page);
    }

    [HttpGet("content/tassa-soggiorno/{comuneSlug}")]
    [ProducesResponseType(typeof(SeoPagePublicDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SeoPagePublicDto>> GetTouristTaxPage(
        string comuneSlug,
        CancellationToken cancellationToken)
    {
        var page = await seoContentService.GetTouristTaxPageAsync(
            comuneSlug,
            AllowDraftPages(),
            cancellationToken);

        return page is null ? NotFound() : Ok(page);
    }

    [HttpPost("tourist-tax/calculate")]
    [EnableRateLimiting("PublicTouristTaxCalc")]
    [ProducesResponseType(typeof(PublicTouristTaxCalculateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PublicTouristTaxCalculateResponse>> CalculateTouristTax(
        [FromBody] PublicTouristTaxCalculateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.NumberOfAdults < 1 || request.CheckOutDate <= request.CheckInDate)
            return BadRequest(new { error = "Invalid calculation parameters." });

        var result = await seoContentService.CalculateTouristTaxAsync(request, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
