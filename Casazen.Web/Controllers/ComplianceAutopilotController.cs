using Casazen.Core.Authorization;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// «Gestisci con AI» of the page of the adempimenti: the assistant takes charge of what it can do in the host's cockpit
/// (<see cref="IComplianceAutopilotService"/>): it asks the guests for their data (the check-in link by e-mail and a message the host sends
/// by WhatsApp or SMS) and closes the departures that are over; the rest it lists as the host's, with the reason.
/// </summary>
/// <remarks>
/// The plan is read with <c>booking.read</c>, like the cockpit it comes from. A run needs <c>booking.write</c> and, for every stay, the same
/// permission on its property as <c>POST /api/bookings/{id}/check-out</c> and <c>POST /api/bookings/{id}/checkin/resend-link</c> (TN-3): a stay
/// of a property the caller may not write on is listed as the host's, never touched. The scope of the cockpit is the caller's own (AM-03).
/// A run that writes chat messages takes one permit of the AI limits of the user and of the org, right before it calls the AI.
/// </remarks>
[ApiController]
[Route("api/compliance/autopilot")]
[Authorize(Policy = CasazenPolicies.BookingRead)]
public class ComplianceAutopilotController(
    IComplianceAutopilotService autopilot,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    AiRequestRateLimiter aiLimiter,
    ILogger<ComplianceAutopilotController> logger) : ControllerBase
{
    /// <summary>Most keys of a run request that are read: a page of the cockpit never holds more.</summary>
    private const int MaxKeys = 200;

    /// <summary>
    /// What the assistant would do now: the items of the cockpit that have a stay, each <c>Ready</c> (the assistant does it), <c>Waiting</c> or
    /// <c>Host</c> (only the host can), with the reason. Nothing is changed and nobody is contacted.
    /// </summary>
    /// <response code="200">The plan.</response>
    /// <response code="401">The caller has no org or no reach on host data.</response>
    [HttpGet]
    [ProducesResponseType(typeof(AutopilotPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AutopilotPlanDto>> GetPlan(CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(cancellationToken) is not { } scope)
            return Unauthorized();

        var plan = await autopilot.PlanAsync(scope, Caller(scope, withAiLimit: false), cancellationToken);
        return Ok(AutopilotPlanDto.From(plan));
    }

    /// <summary>
    /// Runs the plan: asks the guests for their data and closes the departures that are over, for the items that are <c>Ready</c> now (only
    /// the ones in <c>keys</c> when it is given). Each item is its own unit; the answer says how every one ended, with the messages for the
    /// host to send. Not an error when the AI cannot be used: the messages are plain texts and <c>aiReason</c> says why.
    /// </summary>
    /// <response code="200">What was done.</response>
    /// <response code="401">The caller has no org or no reach on host data.</response>
    [HttpPost("run")]
    [Authorize(Policy = CasazenPolicies.BookingWrite)]
    [ProducesResponseType(typeof(AutopilotRunResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AutopilotRunResponse>> Run(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] AutopilotRunRequest? request,
        CancellationToken cancellationToken)
    {
        if (await ResolveScopeAsync(cancellationToken) is not { } scope)
            return Unauthorized();

        var keys = request?.Keys is { Count: > 0 } requested
            ? requested.Where(k => !string.IsNullOrWhiteSpace(k)).Take(MaxKeys).ToList()
            : null;
        var result = await autopilot.RunAsync(scope, Caller(scope, withAiLimit: true), keys, cancellationToken);

        logger.LogInformation(
            "User {UserId} ran the assistant of the adempimenti: {Outcomes} items, AI {AiUsed}",
            User.GetUserId(),
            result.Outcomes.Count,
            result.AiUsed);
        return Ok(AutopilotRunResponse.From(result));
    }

    private async Task<HostScope?> ResolveScopeAsync(CancellationToken cancellationToken)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken);
        return orgId is null ? null : await hostScopeResolver.ResolveHostScopeAsync(User, orgId.Value, cancellationToken);
    }

    /// <summary>
    /// What the caller may do: <c>booking.write</c> on the property of each stay (the check the booking endpoints make), and a permit of the AI
    /// limits when a run calls the AI (the same partition as the other AI endpoints: <c>user:{id}</c> and the org).
    /// </summary>
    private AutopilotCaller Caller(HostScope scope, bool withAiLimit)
    {
        var userKey = User.GetUserId() is { Length: > 0 } userId ? $"user:{userId}" : $"ip:{ClientIp.GetRateLimitKey(HttpContext)}";
        return new AutopilotCaller(
            async (propertyId, ct) =>
                await hostResources.ForPropertyAsync(propertyId, ct) is { } resource
                && await authorizationService.IsAuthorizedAsync(User, resource, BookingOperations.Write),
            withAiLimit ? () => aiLimiter.TryAcquire(userKey, scope.OrgId).IsAllowed : null);
    }
}
