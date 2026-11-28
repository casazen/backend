using System.ComponentModel.DataAnnotations;
using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/properties/{id:guid}/responsible")]
[Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
public class PropertyResponsibleController(
    IPropertyService propertyService,
    IOrgPropertyAccessService propertyAccess,
    IAuthorizationService hostAuthorizationService,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
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

        var property = await propertyService.GetPropertyRecordAsync(id);
        if (property is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await hostAuthorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), SharedPropertyOperations.Write))
            return Forbid();

        await propertyAccess.SetResponsibleAsync(orgId.Value, id, request.UserId, cancellationToken);
        return NoContent();
    }
}

public class SetPropertyResponsibleRequest
{
    [MaxLength(255, ErrorMessage = "PropertyResponsibleInvalid")]
    public string? UserId { get; set; }
}
