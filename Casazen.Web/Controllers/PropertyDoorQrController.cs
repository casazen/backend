using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The address behind the QR code printed for the door of a property (E22/F01/US02/T01, NF-24, NF-50): <c>{App:PublicSiteBaseUrl}/checkin/porta/{doorToken}</c>. The token is random,
/// made the first time the host asks and replaced by <c>POST .../rotate</c> («Rigenera»), after which the printed code of the old one stops working at once. Behind
/// <see cref="FeatureFlags.CheckInIdentityVerification"/> (404 while it is off) and the policy <see cref="CasazenPolicies.PropertyWrite"/>, then the property is authorized like the other
/// property writes: a property of another org is a 404, a property of the same org that the caller may not manage (a collaborator «Solo alcuni» without it) a 403.
/// </summary>
/// <remarks>The answer holds the door token, so it is private and never cached. Every change is logged with the user and the property, never with the token.</remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/door-qr")]
[FeatureGate(FeatureFlags.CheckInIdentityVerification)]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
[PrivateAnswer]
public sealed class PropertyDoorQrController(
    IDoorCheckInService doorCheckIn,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    PublicSiteLinks siteLinks,
    ILogger<PropertyDoorQrController> logger) : ControllerBase
{
    /// <summary>
    /// The address of the door page of the property. The first time it is asked the token is made (the call is safe to repeat: it never changes a token that exists, <c>rotate</c> does).
    /// 404 <c>property_not_found</c>; 403.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DoorQrDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DoorQrDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        var (resource, denied) = await AuthorizeAsync(propertyId, cancellationToken);
        if (denied is not null)
            return denied;

        // A missing App:PublicSiteBaseUrl is a configuration error (500) before any token is made: never a wrong address.
        siteLinks.EnsureConfigured();

        var token = await doorCheckIn.GetOrCreateTokenAsync(resource!.OrgId, propertyId, cancellationToken);
        return token is null ? PropertyNotFound() : Ok(new DoorQrDto { Url = siteLinks.DoorCheckIn(token) });
    }

    /// <summary>
    /// Replaces the token: the printed code of the old one stops working at once (the public page answers it like a code that never existed). 404 <c>property_not_found</c>; 403.
    /// </summary>
    [HttpPost("rotate")]
    [ProducesResponseType(typeof(DoorQrDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DoorQrDto>> Rotate(Guid propertyId, CancellationToken cancellationToken)
    {
        var (resource, denied) = await AuthorizeAsync(propertyId, cancellationToken);
        if (denied is not null)
            return denied;

        siteLinks.EnsureConfigured();

        var token = await doorCheckIn.RotateTokenAsync(resource!.OrgId, propertyId, cancellationToken);
        if (token is null)
            return PropertyNotFound();

        logger.LogInformation("Door QR code of property {PropertyId} replaced by user {UserId}", propertyId, User.GetUserId());
        return Ok(new DoorQrDto { Url = siteLinks.DoorCheckIn(token) });
    }

    /// <summary>The property as the authorization sees it, or the answer to give: 404 when it is not one of the caller's org (the tenant filter hides it), 403 when the caller may not write it.</summary>
    private async Task<(HostResource? Property, ActionResult? Denied)> AuthorizeAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
            return (null, PropertyNotFound());

        if (!await authorizationService.IsAuthorizedAsync(User, property, PropertyOperations.Write))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the door QR code of property {PropertyId}",
                User.GetUserId(), PropertyOperations.Write.PermissionKey, propertyId);
            return (null, Forbid());
        }

        return (property, null);
    }

    private ObjectResult PropertyNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");
}
