using System.ComponentModel.DataAnnotations;
using Casazen.Core.DTOs;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/orgs")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.PublicRead)]
public class PublicOrgController(
    IOrgService orgService,
    IPropertyService propertyService,
    IEntitlementService entitlementService,
    PublicSiteLinks publicSiteLinks) : ControllerBase
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
        dto.CanonicalUrl = publicSiteLinks.TryPublicPage(PublicSitePaths.Org(org.Slug));
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

        property.CanonicalUrl = publicSiteLinks.TryPublicPage(
            PublicSitePaths.Property(org.Slug, string.IsNullOrWhiteSpace(property.Slug) ? property.Id.ToString() : property.Slug));
        return Ok(property);
    }
}
