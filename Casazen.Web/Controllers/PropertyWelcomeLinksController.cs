using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Crm;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The welcome link of a property (E07/F04/US01/T03, NF-90, CRM-03): the address behind the QR code the host prints for the house, where a
/// guest leaves a contact and the consents for it (<c>api/public/welcome/{token}</c>). A property has one active link; rotating it revokes the
/// old one at once. Behind <c>Features:CrmMarketing</c> (404 while it is off, before authentication) and the policy
/// <see cref="CasazenPolicies.PropertyWrite"/>, then the property is authorized like the other property writes: a property of another org is
/// a 404, a visible property the caller may not edit (a collaborator with the permission on other properties only) is a 403. The public side
/// is not behind the flag: a code already printed keeps working. Runbook <c>docs/runbooks/crm-consent.md</c>.
/// </summary>
[ApiController]
[Route("api/properties/{propertyId:guid}/welcome-link")]
[FeatureGate(FeatureFlags.CrmMarketing)]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
public class PropertyWelcomeLinksController(
    IPropertyWelcomeLinkService links,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    PublicSiteLinks siteLinks,
    ILogger<PropertyWelcomeLinksController> logger) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(WelcomeLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WelcomeLinkDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(propertyId, cancellationToken);
        if (denied is not null)
            return denied;

        var link = await links.GetActiveAsync(property!.OrgId, propertyId, cancellationToken);
        return Ok(WelcomeLinkDto.From(link, siteLinks));
    }

    [HttpPost]
    [ProducesResponseType(typeof(WelcomeLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(WelcomeLinkDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WelcomeLinkDto>> CreateOrGet(Guid propertyId, CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(propertyId, cancellationToken);
        if (denied is not null)
            return denied;

        var result = await links.CreateOrGetAsync(property!.OrgId, propertyId, User.GetUserId(), cancellationToken);
        var dto = WelcomeLinkDto.From(result.Link, siteLinks);
        if (!result.Created)
            return Ok(dto);

        logger.LogInformation("Welcome link of property {PropertyId} made by user {UserId}", propertyId, User.GetUserId());
        return Created($"/api/properties/{propertyId}/welcome-link", dto);
    }

    [HttpPost("rotate")]
    [ProducesResponseType(typeof(WelcomeLinkDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<WelcomeLinkDto>> Rotate(Guid propertyId, CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(propertyId, cancellationToken);
        if (denied is not null)
            return denied;

        var link = await links.RotateAsync(property!.OrgId, propertyId, User.GetUserId(), cancellationToken);
        logger.LogInformation("Welcome link of property {PropertyId} rotated by user {UserId}", propertyId, User.GetUserId());
        return Created($"/api/properties/{propertyId}/welcome-link", WelcomeLinkDto.From(link, siteLinks));
    }

    private async Task<(HostResource? Property, ActionResult? Denied)> AuthorizeAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return (null, this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound"));

        if (!await authorizationService.IsAuthorizedAsync(User, property, PropertyOperations.Write))
        {
            logger.LogWarning("User {UserId} denied {Permission} on the welcome link of property {PropertyId}", User.GetUserId(), PropertyOperations.Write.PermissionKey, propertyId);
            return (null, Forbid());
        }

        return (property, null);
    }
}
