using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Onboarding;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The regional ISTAT credentials of a property (E20/F03/US01/T01): <b>write-only</b>, with the same policy as the Alloggiati Web credentials
/// (<see cref="QuesturaCredentialsController"/>). The host sets or replaces the user name, the password and the code of the structure, or removes
/// them; every answer says only whether they are configured and since when, never a value. CasaZen support opens them only on an open «Do it for
/// me» request, with a reason and an audit line (<c>POST api/admin/onboarding/assist-requests/{id}/reveal</c>).
/// </summary>
/// <remarks>
/// <para>TN-3: the property is authorized as a <see cref="HostResource"/> (org, <c>property.*</c> permission, owner of the property or org-wide role).
/// Another org's property answers 404, a property of the same org the caller may not manage answers 403. Every change is logged with the user and
/// the property, never with a value.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c>, like the support vault: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/istat-credentials")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[PrivateAnswer]
public sealed class PropertyIstatCredentialsController(
    IPropertyIstatCredentialsService credentialsService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<PropertyIstatCredentialsController> logger) : ControllerBase
{
    /// <summary>Whether the property has ISTAT credentials, and since when.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IstatCredentialsStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IstatCredentialsStatusDto>> GetStatus(Guid propertyId)
    {
        var (_, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var status = await credentialsService.GetStatusAsync(propertyId, HttpContext.RequestAborted);
        return Ok(IstatCredentialsStatusDto.From(status));
    }

    /// <summary>Sets or replaces the ISTAT credentials of the property. Answers the new status, never the values.</summary>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(IstatCredentialsStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IstatCredentialsStatusDto>> Set(Guid propertyId, [FromBody] SetIstatCredentialsRequest request)
    {
        var (resource, denied) = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write);
        if (denied is not null)
            return denied;

        var status = await credentialsService.SetAsync(propertyId, resource!.OrgId, request.ToInput(), HttpContext.RequestAborted);
        logger.LogInformation("User {UserId} set the ISTAT credentials of property {PropertyId}", User.GetUserId(), propertyId);
        return Ok(IstatCredentialsStatusDto.From(status));
    }

    /// <summary>Removes the ISTAT credentials of the property (204, also when there were none).</summary>
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
            logger.LogInformation("User {UserId} removed the ISTAT credentials of property {PropertyId}", User.GetUserId(), propertyId);

        return NoContent();
    }

    private async Task<(HostResource? Resource, ActionResult? Denied)> AuthorizePropertyAsync(Guid propertyId, HostOperationRequirement operation)
    {
        // Tenant filter: another org's property is not found.
        var resource = await hostResources.ForPropertyAsync(propertyId, HttpContext.RequestAborted);
        if (resource is null)
            return (null, this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound"));

        if (!await authorizationService.IsAuthorizedAsync(User, resource, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the ISTAT credentials of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return (resource, Forbid());
        }

        return (resource, null);
    }
}
