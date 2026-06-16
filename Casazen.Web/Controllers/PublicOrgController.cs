using System.ComponentModel.DataAnnotations;
using Casazen.Core.DTOs;
using Casazen.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/public/orgs")]
[AllowAnonymous]
public class PublicOrgController(IOrgService orgService, IPropertyService propertyService) : ControllerBase
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

        return Ok(new PublicOrgDto
        {
            Slug = org.Slug,
            DisplayName = org.DisplayName,
            LogoUrl = org.LogoUrl,
            ThemeColor = org.ThemeColor,
            ContactEmail = org.ContactEmail,
        });
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

    [HttpGet("{slug}/properties/{propertyId:guid}")]
    [ProducesResponseType(typeof(PublicPropertyDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PublicPropertyDetailDto>> GetProperty(
        [StringLength(100)] string slug,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        var org = await orgService.GetPublicBySlugAsync(slug, cancellationToken);
        if (org is null)
            return NotFound();

        var property = await propertyService.GetPublicPropertyForOrgAsync(propertyId, org.Id);
        if (property is null)
            return NotFound();

        return Ok(property);
    }
}
