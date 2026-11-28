using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

[ApiController]
[Route("api/internal-activities")]
[Authorize(Policy = CasazenPolicies.AgencyActivityManage)]
public class InternalActivitiesController(
    IInternalActivityService activityService,
    IHostScopeResolver hostScopeResolver,
    IOrgContextResolver orgContextResolver,
    IStayLifecycleService? stayLifecycle = null,
    ILogger<InternalActivitiesController>? logger = null) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(InternalActivity), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InternalActivity>> CreateActivity(
        [FromBody] CreateActivityRequest request)
    {
        if (GetUserId() is not { } userId)
            return Unauthorized();

        var activity = await activityService.CreateActivityAsync(
            request.PropertyId,
            request.StaffUserId,
            request.ActivityType,
            request.ScheduledAt,
            request.InternalRate,
            request.RechargeToOwner,
            request.BookingId,
            request.Notes,
            userId);

        return CreatedAtAction(
            nameof(GetActivity),
            new { id = activity.Id },
            activity);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<InternalActivity>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<InternalActivity>>> GetActivities(
        [FromQuery] Guid? propertyId = null,
        [FromQuery] string? staffUserId = null)
    {
        if (GetUserId() == null)
            return Unauthorized();

        var cancellationToken = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);

        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return Ok(Array.Empty<InternalActivity>());

        var activities = await activityService.GetActivitiesAsync(
            scope,
            propertyId,
            staffUserId,
            cancellationToken);

        return Ok(activities);
    }

    [HttpPut("{id}/status")]
    [ProducesResponseType(typeof(InternalActivity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InternalActivity>> UpdateStatus(
        Guid id,
        [FromBody] UpdateStatusRequest request)
    {
        if (GetUserId() == null)
            return Unauthorized();

        var activity = await activityService.UpdateStatusAsync(
            id,
            request.Status,
            request.Notes);

        await FollowCleaningDoneAsync(activity);
        return Ok(activity);
    }

    private async Task FollowCleaningDoneAsync(InternalActivity activity)
    {
        if (stayLifecycle is null
            || activity.BookingId is not { } bookingId
            || activity.Action is not null
            || !string.Equals(activity.ActivityType, InternalActivity.CleaningActivityType, StringComparison.Ordinal)
            || !string.Equals(activity.Status, InternalActivity.StatusCompleted, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await stayLifecycle.DeclarePropertyReadyAutomaticallyAsync(bookingId, HttpContext.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError(ex, "The property of booking {BookingId} could not be declared ready after its cleaning {ActivityId}", bookingId, activity.Id);
        }
    }

    [HttpGet("my-activities")]
    [ProducesResponseType(typeof(IReadOnlyList<InternalActivity>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<IReadOnlyList<InternalActivity>>> GetMyActivities()
    {
        if (GetUserId() is not { } userId)
            return Unauthorized();

        var cancellationToken = HttpContext.RequestAborted;
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);

        if (orgId is null
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken) is not { } scope)
            return Ok(Array.Empty<InternalActivity>());

        var activities = await activityService.GetMyActivitiesAsync(
            scope,
            userId,
            cancellationToken);

        return Ok(activities);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(InternalActivity), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InternalActivity>> GetActivity(Guid id)
    {
        if (GetUserId() == null)
            return Unauthorized();

        var activity = await activityService.GetActivityAsync(id, HttpContext.RequestAborted);

        if (activity == null)
            return NotFound();

        return Ok(activity);
    }

    private string? GetUserId() => User.GetUserId();
}

public record CreateActivityRequest(
    Guid PropertyId,
    string StaffUserId,
    string ActivityType,
    DateTime ScheduledAt,
    decimal? InternalRate = null,
    bool RechargeToOwner = false,
    Guid? BookingId = null,
    string? Notes = null);

public record UpdateStatusRequest(
    string Status,
    string? Notes = null);
