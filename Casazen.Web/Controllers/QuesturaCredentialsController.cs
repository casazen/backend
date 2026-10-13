using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Alloggiati Web (Questura) credentials of a property (CO-14, A5-30): <b>write-only</b>. The host sets or replaces
/// username, password and WSKey, or removes them; every answer says only whether they are configured and since when,
/// never a value. The values are encrypted at rest (<c>docs/runbooks/encryption.md</c>).
/// </summary>
/// <remarks>
/// TN-3: the property is authorized as a <see cref="HostResource"/> (org, <c>property.*</c> permission, owner of the
/// property or org-wide role). Another org's property answers 404, a property of the same org the caller may not
/// manage answers 403. Every change is logged with the user and the property (audit), never with a value.
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/questura-credentials")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class QuesturaCredentialsController(
    IQuesturaCredentialsService credentialsService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<QuesturaCredentialsController> logger) : ControllerBase
{
    /// <summary>Whether the property has Alloggiati Web credentials, and since when.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(QuesturaCredentialsStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QuesturaCredentialsStatusDto>> GetStatus(Guid propertyId)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var status = await credentialsService.GetStatusAsync(propertyId, HttpContext.RequestAborted);
        return Ok(QuesturaCredentialsStatusDto.From(status));
    }

    /// <summary>Sets or replaces the three credentials of the property. Answers the new status, never the values.</summary>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(QuesturaCredentialsStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QuesturaCredentialsStatusDto>> Set(
        Guid propertyId,
        [FromBody] SetQuesturaCredentialsRequest request)
    {
        var (resource, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null)
            return denied;

        var status = await credentialsService.SetAsync(
            propertyId, resource!.OrgId, request.ToInput(), HttpContext.RequestAborted);
        logger.LogInformation(
            "User {UserId} set the Questura credentials of property {PropertyId}", User.GetUserId(), propertyId);
        return Ok(QuesturaCredentialsStatusDto.From(status));
    }

    /// <summary>Removes the credentials of the property (204, also when there were none).</summary>
    [HttpDelete]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid propertyId)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null)
            return denied;

        if (await credentialsService.DeleteAsync(propertyId, HttpContext.RequestAborted))
        {
            logger.LogInformation(
                "User {UserId} removed the Questura credentials of property {PropertyId}", User.GetUserId(), propertyId);
        }

        return NoContent();
    }

    private async Task<(HostResource? Resource, ActionResult? Denied)> AuthorizePropertyAsync(
        Guid propertyId,
        HostOperationRequirement operation)
    {
        // Tenant filter: another org's property is not found.
        var resource = await hostResources.ForPropertyAsync(propertyId, HttpContext.RequestAborted);
        if (resource is null)
            return (null, this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound"));

        if (!await authorizationService.IsAuthorizedAsync(User, resource, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the Questura credentials of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return (resource, Forbid());
        }

        return (resource, null);
    }
}
