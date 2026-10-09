using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The member in charge of a property (AM-03): the one who is told, together with the org's administrators, when something
/// happens on it (a new booking, an update of a supplier). Whoever may change the property core changes it
/// (<see cref="CasazenPolicies.SharedPropertyWrite"/> and the same property check as <c>PUT /api/properties/{id}</c>), and
/// only to an active member of the org who reaches the property. Read it from <c>PropertyResponse.ResponsibleUserId</c>.
/// </summary>
[ApiController]
[Route("api/properties/{id:guid}/responsible")]
[Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
public class PropertyResponsibleController(
    IPropertyService propertyService,
    IOrgPropertyAccessService propertyAccess,
    IAuthorizationService hostAuthorizationService,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// Puts <c>userId</c> in charge of the property, or nobody with <c>null</c> (the creator is told again). 403 without
    /// <c>property.write</c> on it, 404 <c>property_not_found</c>, 422 <c>property_responsible_invalid</c> when the person is not
    /// an active member of the org who reaches the property. 204 on success.
    /// </summary>
    [HttpPut]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Set(Guid id, [FromBody] SetPropertyResponsibleRequest request, CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        if (orgId is null)
            return Unauthorized();

        // The row alone (tenant filter: another org's property is not found).
        var property = await propertyService.GetPropertyRecordAsync(id);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Write))
            return Forbid();

        await propertyAccess.SetResponsibleAsync(orgId.Value, id, request.UserId, cancellationToken);
        return NoContent();
    }
}

/// <summary>Body of <c>PUT /api/properties/{id}/responsible</c>.</summary>
public class SetPropertyResponsibleRequest
{
    /// <summary>The member to put in charge (<c>User.Id</c>, as <c>OrgMemberDto.UserId</c>); <c>null</c> = nobody.</summary>
    [MaxLength(255, ErrorMessage = "PropertyResponsibleInvalid")]
    public string? UserId { get; set; }
}
