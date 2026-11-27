using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the ISTAT communication of a property is (E21/F01/US01/T01, decision O6, NF-44, NF-52): from the region of its comune, the system and portal of the
/// region, its deadline, the statistical fields its guests are asked, whether the channel manager covers it, who sends it and what the host does next
/// (<c>ConnectInPanel</c> when covered, <c>ManualPortal</c> when not, <c>AskSupport</c> when it is not verified). It reads the table and sends nothing.
/// What the table does not state is «da verificare», never covered.
/// </summary>
/// <remarks>
/// <para><b>Authorization (TN-3).</b> <see cref="CasazenPolicies.PropertyRead"/> and the row-level check of the property
/// (<see cref="PropertyOperations.Read"/>): another org's property (or one that does not exist) answers 404 <c>property_not_found</c>, a property of the org
/// the caller does not reach answers 403, a collaborator limited to some properties included.</para>
/// <para>Behind <c>Features:OctorateOnboarding</c> (404 while it is off): it serves the guided wizard of the sender. The answer is private and never
/// cached; it holds no credential and no personal data.</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/istat-coverage")]
[FeatureGate(FeatureFlags.OctorateOnboarding)]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[PrivateAnswer]
public sealed class PropertyIstatCoverageController(
    IIstatRegionService regions,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<PropertyIstatCoverageController> logger) : ControllerBase
{
    /// <summary>The ISTAT coverage of the property: its region, system, deadline, required guest fields, coverage, sender and next step.</summary>
    /// <response code="200">The coverage of the property.</response>
    /// <response code="403">The caller does not reach the property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(IstatCoverageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IstatCoverageDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        // Tenant filter: another org's property is not found, the same answer as a property that does not exist.
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Read))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the ISTAT coverage of property {PropertyId}",
                User.GetUserId(), PropertyOperations.Read.PermissionKey, propertyId);
            return Forbid();
        }

        var view = await regions.GetCoverageAsync(propertyId, cancellationToken);
        return Ok(IstatCoverageDto.From(view));
    }
}
