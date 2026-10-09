using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The scheduled change of rental mode of a property (PM-02, decisions D16 and D19): the owner programs the passage from
/// short stays to long-term leases, or back, for a day, sees first what stands in the way, and can withdraw it until it
/// happens. The property changes mode by itself at midnight of Rome (<c>property-mode-change</c> job). Behind the
/// <see cref="FeatureFlags.PropertyModeChange"/> flag: off, every action answers 404 before anything is read. Runbook:
/// <c>docs/runbooks/property-rental-mode.md</c>.
/// </summary>
/// <remarks>
/// <para>TN-3. The property core is shared by short-rent hosts and long-term landlords (A7-06) and the change goes both
/// ways, so the policies are the shared ones: <see cref="CasazenPolicies.SharedPropertyRead"/> to read the state,
/// <see cref="CasazenPolicies.SharedPropertyWrite"/> for the preview, the creation and the cancellation, plus the check on
/// the property row (<see cref="SharedPropertyOperations"/>: its owner or an org-wide role, in the caller's org). There is
/// no dedicated "change the mode" permission: the permissions of the team (<c>org.*</c>, AM-01) are not in this base, and
/// the mode is a field of the property, so whoever may write the property may change it. A collaborator with
/// <c>property.read</c> only reads the state. The preview lists stays and leases, so it asks for the write permission too.
/// A property of another org is invisible (404).</para>
/// <para>Errors are ProblemDetails with the codes of <see cref="PropertyModeErrorCodes"/> (FD-05).</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/mode")]
[Authorize(Policy = CasazenPolicies.SharedPropertyRead)]
[FeatureGate(FeatureFlags.PropertyModeChange)]
public class PropertyModeController(
    IPropertyModeService modeService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    ILogger<PropertyModeController> logger) : ControllerBase
{
    /// <summary>The mode of the property, the change waiting for its day and the last one that is over.</summary>
    /// <response code="200">The state.</response>
    /// <response code="403">The caller may not read this property.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PropertyModeStateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyModeStateResponse>> GetState(Guid propertyId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, SharedPropertyOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        var state = await modeService.GetStateAsync(propertyId, cancellationToken);
        return Ok(PropertyModeStateResponse.From(state));
    }

    /// <summary>
    /// What a change to <paramref name="to"/> on <paramref name="date"/> would meet today: the first day possible, the stays,
    /// imported calendar blocks or leases that stand in the way of <paramref name="date"/> (the first day possible when it
    /// is not given) and the <c>issues</c> that would refuse it. Reads only. 200 even when the change is not possible: the
    /// answer says why.
    /// </summary>
    /// <param name="propertyId">The property.</param>
    /// <param name="to"><c>short</c> or <c>long</c> (case-insensitive): the mode asked for.</param>
    /// <param name="date">The first day in the new mode (<c>2026-12-01</c>); optional.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <response code="400"><c>property_mode_target_invalid</c>: <paramref name="to"/> is not a mode; or <c>validation_error</c>: <paramref name="date"/> is not a date.</response>
    /// <response code="404">No property with this id in the caller's org.</response>
    /// <response code="422"><c>property_mode_unchanged</c>: the property is already in that mode.</response>
    [HttpGet("preview")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(typeof(PropertyModePreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertyModePreviewResponse>> Preview(
        Guid propertyId,
        [FromQuery] string? to,
        [FromQuery] DateOnly? date,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, SharedPropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        if (!EnumNames.TryParseDefined<RentalMode>(to, out var target))
        {
            return this.ApiProblem(
                StatusCodes.Status400BadRequest,
                PropertyModeErrorCodes.TargetInvalid,
                PropertyModeErrorCodes.TargetInvalidMessageKey);
        }

        var preview = await modeService.PreviewAsync(propertyId, target, date is { } day ? AsStayDate(day) : null, cancellationToken);
        return Ok(PropertyModePreviewResponse.From(preview));
    }

    /// <summary>
    /// Programs the change: 201 with the change (<c>Scheduled</c>). The host gets an e-mail. To long-term the calendar closes
    /// from the day. 404 <c>property_not_found</c>; 409 <c>property_mode_change_exists</c>,
    /// <c>property_mode_blocked_by_bookings</c>, <c>property_mode_blocked_by_lease</c>, <c>property_mode_blocked_by_draft_lease</c>;
    /// 422 <c>property_mode_unchanged</c>, <c>property_mode_date_too_early</c>, <c>property_mode_date_too_far</c>.
    /// </summary>
    [HttpPost("change")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(typeof(PropertyModeChangeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertyModeChangeDto>> Schedule(
        Guid propertyId,
        [FromBody] ScheduleModeChangeRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, SharedPropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // Logged by the service, with the day and both modes.
        var change = await modeService.ScheduleAsync(
            propertyId, request.To!.Value, AsStayDate(request.EffectiveDate!.Value), userId, cancellationToken);

        return Created($"/api/properties/{propertyId}/mode", PropertyModeChangeDto.From(change));
    }

    /// <summary>
    /// Withdraws a change that is still waiting for its day: 204. 404 <c>property_mode_change_not_found</c> (also a change of
    /// another property); 409 <c>property_mode_change_not_scheduled</c> when it is already applied, cancelled or failed.
    /// </summary>
    [HttpDelete("change/{changeId:guid}")]
    [Authorize(Policy = CasazenPolicies.SharedPropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid propertyId, Guid changeId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, SharedPropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        var userId = User.GetUserId();
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        // Logged by the service.
        await modeService.CancelAsync(propertyId, changeId, userId, cancellationToken);

        return NoContent();
    }

    /// <summary>A date from the client as a stay date: midnight UTC of that day (the storage convention).</summary>
    private static DateTime AsStayDate(DateOnly day) => DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    /// <summary>TN-3: 404 when the property is not visible (another org), 403 when it is but the operation is not allowed.</summary>
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
                PropertyModeErrorCodes.PropertyNotFound,
                PropertyModeErrorCodes.PropertyNotFoundMessageKey);
        }

        if (!await authorizationService.IsAuthorizedAsync(User, property, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the rental mode of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }
}
