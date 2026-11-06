using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Whether the police (Alloggiati Web) and ISTAT send of a property is really active (CHK-12 point 7, NF-52, E03/F02/US01/T02): who sends,
/// the state, what Octorate says about its police account and whether CasaZen holds credentials to download the receipts. What Octorate does
/// not let CasaZen read is <c>Unknown</c>, never <c>Active</c>.
/// </summary>
/// <remarks>
/// <para><b>Authorization (TN-3).</b> <see cref="CasazenPolicies.PropertyRead"/> and the row-level check of the property
/// (<see cref="PropertyOperations.Read"/>): another org's property (or one that does not exist) answers 404 <c>property_not_found</c>, a
/// property of the org the caller does not reach answers 403.</para>
/// <para>The answer is <c>private, no-store</c>. It holds no credential, token or web service key: the Questura credentials are only said to
/// exist, and nothing of the sort is logged.</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/compliance/connection-health")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class ComplianceConnectionHealthController(
    IComplianceConnectionHealthService connectionHealth,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<ComplianceConnectionHealthController> logger) : ControllerBase
{
    /// <summary>
    /// The health of the connection of the property, read now: for the Alloggiati and the ISTAT communication the sender, the state, the
    /// validity, the automatic send and its last run, plus whether the Questura credentials are kept in CasaZen and whether they are usable
    /// (never, in this release). For a channel-manager property it reads the police account of the accommodation once, bounded in time.
    /// </summary>
    /// <response code="200">The connection health of the property.</response>
    /// <response code="403">The caller does not reach the property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ComplianceConnectionHealthDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ComplianceConnectionHealthDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        // Tenant filter: another org's property is not found, the same answer as a property that does not exist.
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Read))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the connection health of property {PropertyId}",
                User.GetUserId(), PropertyOperations.Read.PermissionKey, propertyId);
            return Forbid();
        }

        var health = await connectionHealth.GetAsync(propertyId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(ComplianceConnectionHealthDto.From(health));
    }
}
