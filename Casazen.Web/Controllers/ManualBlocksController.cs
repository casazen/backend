using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Dates the host closes by hand on a property: owner stay, maintenance, other (PC-09, A2-25). A manual block takes its
/// nights everywhere a booking would (booking site, host and public bookings, export to the OTAs) through the single
/// occupancy rule (<see cref="PropertyOccupancy"/>); the host calendar (<c>GET /api/bookings/calendar</c>) shows it as an
/// <c>ical-block</c> item with <c>blockSource: "Manual"</c> and its <c>blockReason</c>. Runbook: docs/runbooks/ical.md
/// "Manual blocks (PC-09)".
/// </summary>
/// <remarks>
/// TN-3: <see cref="CasazenPolicies.PropertyRead"/> to list, <see cref="CasazenPolicies.PropertyWrite"/> to create or
/// remove, plus the check on the property (<c>property.read</c> / <c>property.write</c>). A property or block of another
/// org is invisible (404). Errors are ProblemDetails with the codes of <see cref="ManualBlockErrorCodes"/> (FD-05).
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/blocks")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class ManualBlocksController(
    ICalendarBlockService calendarBlocks,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<ManualBlocksController> logger) : ControllerBase
{
    /// <summary>
    /// Manual blocks of the property with a night from <paramref name="from"/> (included) to <paramref name="to"/>
    /// (excluded), stay dates; without <paramref name="from"/> the blocks not over yet (today in Europe/Rome).
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ManualBlockDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<ManualBlockDto>>> List(
        Guid propertyId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, PropertyOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        if (from is { } fromDate && to is { } toDate && toDate.Date <= fromDate.Date)
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                ManualBlockErrorCodes.InvalidRange,
                ManualBlockErrorCodes.InvalidRangeMessageKey);
        }

        var blocks = await calendarBlocks.ListManualAsync(propertyId, from, to, cancellationToken);
        return Ok(blocks.Select(ManualBlockDto.From).ToList());
    }

    /// <summary>
    /// Closes the nights: 201 with the block. 404 <c>property_not_found</c>; 409
    /// <c>calendar_block_overlaps_booking</c>, <c>calendar_block_overlaps_block</c>; 422
    /// <c>calendar_block_invalid_range</c>, <c>calendar_block_in_past</c>, <c>calendar_block_too_long</c>,
    /// <c>calendar_block_invalid_reason</c>, <c>calendar_block_note_too_long</c>.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ManualBlockDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ManualBlockDto>> Create(
        Guid propertyId,
        [FromBody] CreateManualBlockRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var block = await calendarBlocks.CreateAsync(
            new ManualBlockRequest(propertyId, request.StartDate!.Value, request.EndDate!.Value, request.Reason!.Value, request.Note),
            cancellationToken);

        logger.LogInformation(
            "Manual block {BlockId} created by user {UserId} on property {PropertyId}", block.Id, User.GetUserId(), propertyId);
        return Created($"/api/properties/{propertyId}/blocks/{block.Id}", ManualBlockDto.From(block));
    }

    /// <summary>
    /// Removes a manual block: its nights are free again. 204. 404 <c>calendar_block_not_found</c> (also a block of
    /// another property); 422 <c>calendar_block_not_manual</c> for a block imported from an iCal feed.
    /// </summary>
    [HttpDelete("{blockId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Delete(Guid propertyId, Guid blockId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        var block = await calendarBlocks.FindAsync(blockId, cancellationToken);
        if (block is null || block.PropertyId != propertyId)
        {
            return this.ApiProblem(
                StatusCodes.Status404NotFound, ManualBlockErrorCodes.NotFound, ManualBlockErrorCodes.NotFoundMessageKey);
        }

        await calendarBlocks.DeleteAsync(blockId, cancellationToken);
        logger.LogInformation(
            "Manual block {BlockId} removed by user {UserId} from property {PropertyId}", blockId, User.GetUserId(), propertyId);
        return NoContent();
    }

    private async Task<ActionResult?> AuthorizeAsync(
        Guid propertyId,
        HostOperationRequirement operation,
        CancellationToken cancellationToken)
    {
        var property = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (property is null)
        {
            return this.ApiProblem(
                StatusCodes.Status404NotFound,
                ManualBlockErrorCodes.PropertyNotFound,
                ManualBlockErrorCodes.PropertyNotFoundMessageKey);
        }

        if (!await authorizationService.IsAuthorizedAsync(User, property, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the manual blocks of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }
}
