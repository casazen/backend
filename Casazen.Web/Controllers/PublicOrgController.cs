using System.ComponentModel.DataAnnotations;
using Casazen.Core.DTOs;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Web.DTOs.Orgs;
using Casazen.Infrastructure.Services;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/orgs")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicOrgController(
    IOrgService orgService,
    IPropertyService propertyService,
    IEntitlementService entitlementService,
    PublicOrgSiteUrls siteUrls,
    IOrgSiteDocumentService siteDocumentService,
    IOptions<GdprOptions> gdprOptions) : ControllerBase
{
    [HttpGet("{slug}")]
    [ProducesResponseType(typeof(PublicOrgDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicOrgDto>> GetOrg(
        [StringLength(100)] string slug,
        CancellationToken cancellationToken)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var dto = PublicOrgDto.FromOrg(org, entitlementService.ResolveEffectiveTier(org));
        dto.CanonicalUrl = siteUrls.TryLandingUrl(org);
        // DB-03: the checkout offers the "send me offers" box only when its text has a version (CO-15), like the check-in portal.
        dto.MarketingConsentVersion = GdprOptions.Normalize(gdprOptions.Value.MarketingConsentVersion);
        return Ok(dto);
    }

    [HttpGet("{slug}/properties")]
    [ProducesResponseType(typeof(IEnumerable<PublicPropertyDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<PublicPropertyDto>>> GetProperties(
        [StringLength(100)] string slug,
        CancellationToken cancellationToken)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var properties = await propertyService.SearchByOrgAsync(org.Id, cancellationToken);
        return Ok(properties);
    }

    [HttpGet("{slug}/properties/{propertySlugOrId}")]
    [ProducesResponseType(typeof(PublicPropertyDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicPropertyDetailDto>> GetProperty(
        [StringLength(100)] string slug,
        [StringLength(100)] string propertySlugOrId,
        CancellationToken cancellationToken)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var property = await propertyService.GetPublicPropertyForOrgAsync(propertySlugOrId, org.Id);
        if (property is null)
            return NotFound();

        property.CanonicalUrl = siteUrls.TryPropertyUrl(
            org, string.IsNullOrWhiteSpace(property.Slug) ? property.Id.ToString() : property.Slug);
        return Ok(property);
    }

    /// <summary>
    /// The operator's privacy notice or booking terms (BK-14, A3-21): <c>kind</c> is <c>privacy</c> or <c>terms</c>.
    /// 200 with <c>published: false</c> when the operator has not published it (or withdrew it): that is a normal state of
    /// the page, not an error. 404 for an unknown org or kind.
    /// </summary>
    [HttpGet("{slug}/documents/{kind}")]
    [ProducesResponseType(typeof(PublicOrgDocumentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicOrgDocumentDto>> GetDocument(
        [StringLength(100)] string slug,
        [StringLength(20)] string kind,
        CancellationToken cancellationToken)
    {
        if (!OrgSiteDocumentRules.TryParseKind(kind, out var parsedKind))
            return NotFound();

        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var document = await siteDocumentService.GetPublishedAsync(org.Id, parsedKind, cancellationToken);
        return Ok(document is null ? PublicOrgDocumentDto.NotPublished(parsedKind) : PublicOrgDocumentDto.From(document));
    }
}
