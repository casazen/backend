using System.Security.Claims;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The listing data of a unit that the channels ask for and CasaZen lacked (NF-34, NF-35): its rooms with their beds and its length-of-stay
/// discounts. Short-rent only, like every short-stay side of a property: the permissions are <c>property.read</c> / <c>property.write</c> of the
/// short-rent context and the row is authorized like the photos and the site profile (the property of the caller's org, a colleague's one only
/// with a role that reaches it, TN-3): a property of another org answers 404.
/// </summary>
[ApiController]
[Route("api/properties/{id:guid}")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class PropertyListingDataController(
    IPropertyRoomService rooms,
    IStayDiscountService discounts,
    IPropertyService properties,
    IAuthorizationService hostAuthorization,
    IAdminAccessAuditService adminAccessAuditService,
    ILogger<PropertyListingDataController> logger) : ControllerBase
{
    /// <summary>The rooms of the unit in order, each with its beds; an empty list when the host listed none.</summary>
    /// <response code="200">The rooms.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet("rooms")]
    [ProducesResponseType(typeof(IReadOnlyList<PropertyRoomView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<PropertyRoomView>>> GetRooms(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var list = await rooms.GetAsync(id, cancellationToken);
        return list is null ? NotFound() : Ok(list);
    }

    /// <summary>
    /// Replaces the rooms (at most 20; 1 to 10 beds of a kind in a room; at least one bed in all): a room sent with the <c>id</c> of a stored one
    /// updates it, a stored room not sent is removed and the photos assigned to it lose the assignment. The same save sets the bedrooms of the
    /// property to the number of rooms of type <c>Bedroom</c> and its beds to the number of beds; the maximum of guests, the bathrooms and the area
    /// stay as they are. Answers with the rooms as stored.
    /// </summary>
    /// <response code="200">The rooms as stored.</response>
    /// <response code="400"><c>validation_error</c>: the list is absurdly long, or a type is not one of the lists.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org, or <c>property_room_not_found</c>: a room id that is not one of its rooms.</response>
    /// <response code="422"><c>property_rooms_invalid</c> (too many rooms, a bed count out of range, no bed in all) or <c>property_not_bookable_in_long_mode</c>. Nothing is saved.</response>
    [HttpPut("rooms")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(IReadOnlyList<PropertyRoomView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<PropertyRoomView>>> PutRooms(
        Guid id,
        [FromBody] PropertyRoomsRequest request,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertyListingData.Rooms");
        if (denied is not null)
            return denied;

        var list = await rooms.ReplaceAsync(id, request.ToInput(), cancellationToken);
        if (list is null)
            return NotFound();

        logger.LogInformation("Rooms of property {PropertyId} of org {OrgId} saved", id, property!.OrgId);
        return Ok(list);
    }

    /// <summary>The length-of-stay discounts of the property in order of nights; an empty list when it has none.</summary>
    /// <response code="200">The tiers.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet("stay-discounts")]
    [ProducesResponseType(typeof(IReadOnlyList<StayDiscountTier>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<StayDiscountTier>>> GetStayDiscounts(Guid id, CancellationToken cancellationToken)
    {
        var (_, denied) = await AuthorizeAsync(id, PropertyOperations.Read);
        if (denied is not null)
            return denied;

        var list = await discounts.GetAsync(id, cancellationToken);
        return list is null ? NotFound() : Ok(list);
    }

    /// <summary>
    /// Replaces the length-of-stay discounts (at most 5 tiers, each with its own minimum of 2 to 365 nights and a percentage above 0 and at most
    /// 100 with two decimals; an empty list removes them all). A stay of enough nights gets the tier with the most nights it reaches, off the
    /// lodging only: never off the cleaning fee or the tourist tax. Answers with the tiers as stored.
    /// </summary>
    /// <response code="200">The tiers as stored.</response>
    /// <response code="400"><c>validation_error</c>: the list is absurdly long.</response>
    /// <response code="403">The caller may not change this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="422"><c>property_stay_discounts_invalid</c> (too many tiers, nights or percentage out of range, two tiers with the same nights) or <c>property_not_bookable_in_long_mode</c>. Nothing is saved.</response>
    [HttpPut("stay-discounts")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(IReadOnlyList<StayDiscountTier>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<IReadOnlyList<StayDiscountTier>>> PutStayDiscounts(
        Guid id,
        [FromBody] StayDiscountsRequest request,
        CancellationToken cancellationToken)
    {
        var (property, denied) = await AuthorizeAsync(id, PropertyOperations.Write, "PropertyListingData.StayDiscounts");
        if (denied is not null)
            return denied;

        var list = await discounts.ReplaceAsync(id, request.ToInput(), cancellationToken);
        if (list is null)
            return NotFound();

        logger.LogInformation("Stay discounts of property {PropertyId} of org {OrgId} saved", id, property!.OrgId);
        return Ok(list);
    }

    /// <summary>
    /// TN-3 check, like the photo and the site profile endpoints: the property of the caller's org (the tenant filter hides another org's one, so
    /// 404), the host permission for the row (403), and the audit of an org-wide role acting on a colleague's property.
    /// </summary>
    private async Task<(Property? Property, ActionResult? Denied)> AuthorizeAsync(
        Guid id,
        HostOperationRequirement operation,
        string? auditAction = null)
    {
        var userId = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier")?.Value;
        if (string.IsNullOrEmpty(userId))
            return (null, Unauthorized());

        var property = await properties.GetPropertyRecordAsync(id);
        if (property is null)
            return (null, NotFound());

        if (!await hostAuthorization.IsAuthorizedAsync(User, HostResource.ForProperty(property), operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the listing data of property {PropertyId}",
                userId, operation.PermissionKey, id);
            return (null, Forbid());
        }

        if (auditAction is not null && userId != property.OwnerId && IsOrgWide())
            await adminAccessAuditService.LogPrivilegedPropertyAccessAsync(userId, id, property.OwnerId, auditAction);

        return (property, null);
    }

    private bool IsOrgWide()
    {
        var roles = Auth0RolesClaimParser.Parse(User.FindAll("https://casazen.app/roles").Select(c => c.Value));
        if (roles.Count == 0)
            roles = [.. User.FindAll(ClaimTypes.Role).Select(c => c.Value)];

        return roles.Any(HostRoles.OrgWide.Contains);
    }
}
