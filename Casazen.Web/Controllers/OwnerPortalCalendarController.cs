using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Configuration;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Controllers;

/// <summary>
/// The privacy-safe calendar and the owner stays of the owner portal (E07/F07/US01/T01, NF-111, ADR-011).
/// </summary>
/// <remarks>
/// <para><b>Who.</b> The owner portal policy (<c>context=owner_portal</c>, <c>owner_id</c>, no host role). What the caller reaches is resolved again from the
/// database at every request (<see cref="IOwnerPortalAccessResolver"/>): its ownerships in force and the delegations of the owners that gave it a permission.
/// A property of another owner, an ended ownership and a missing one are the same 404 (ADR-011-R2); a permission the caller holds nowhere is 403
/// <c>owner_portal_permission_missing</c>.</para>
/// <para><b>What.</b> The calendar answers dates and a neutral kind only, never a guest, a channel or a text (AC-NF111-1). A stay is a request until the
/// agency (or the rule) approves it, and reads «confirmed» only once its block is acknowledged.</para>
/// <para>Behind <c>Features:AgencyTier</c>, like the rest of the agency owner portal: 404 while it is off. Every answer is private and never cached.</para>
/// </remarks>
[ApiController]
[Route("api/owner-portal")]
[Authorize(Policy = CasazenPolicies.OwnerPortal)]
[FeatureGate(FeatureFlags.AgencyTier)]
[PrivateAnswer]
[EnableRateLimiting(RateLimitPolicies.OwnerPortalRead)]
public sealed class OwnerPortalCalendarController(
    IOwnerPortalAccessResolver accessResolver,
    IOwnerStayService stays,
    IOptions<Auth0Options> auth0Options) : ControllerBase
{
    private readonly string? _customClaimNamespace = auth0Options.Value.CustomClaimNamespace;

    /// <summary>
    /// The calendar of a property: runs of nights (<c>start</c> included, <c>end</c> excluded) that are <c>available</c>, <c>occupied</c> by a guest,
    /// <c>blocked</c>, or an <c>ownerStay</c> of the caller with its id and state. From <c>from</c> to <c>to</c> (<c>yyyy-MM-dd</c>, both included; today and
    /// the next 90 nights by default, at most 366), cut to the ownerships in force of the caller. Needs <c>calendar.read</c>.
    /// </summary>
    [HttpGet("calendar")]
    [ProducesResponseType(typeof(OwnerCalendar), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerCalendar>> GetCalendar(
        [FromQuery] Guid propertyId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await stays.GetCalendarAsync(access, propertyId, from, to, cancellationToken));
    }

    /// <summary>The owner stays of the owner profiles the caller follows (<c>calendar.read</c> or <c>stays.request</c>), newest arrival first.</summary>
    [HttpGet("stays")]
    [ProducesResponseType(typeof(IReadOnlyList<OwnerStayView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OwnerStayView>>> ListStays(CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await stays.ListForOwnerAsync(access, cancellationToken));
    }

    /// <summary>The rule an owner stay on the property follows, and the nights already held this year. Needs <c>stays.request</c>.</summary>
    [HttpGet("stays/rule")]
    [ProducesResponseType(typeof(OwnerStayRuleSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OwnerStayRuleSummary>> GetRule([FromQuery] Guid propertyId, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await stays.GetRuleForOwnerAsync(access, propertyId, cancellationToken));
    }

    /// <summary>
    /// Asks for an owner stay (<c>startDate</c> the arrival, <c>endDate</c> the departure). 201 with the stay, <c>requested</c>; 422 with the code of the rule
    /// it breaks (<c>owner_stay_notice_too_short</c>, <c>owner_stay_allowance_exceeded</c>, ...); 409 <c>owner_stay_dates_unavailable</c>. Needs
    /// <c>stays.request</c>.
    /// </summary>
    [HttpPost("stays")]
    [ProducesResponseType(typeof(OwnerStayView), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OwnerStayView>> RequestStay([FromBody] OwnerStayRequestBody body, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        var stay = await stays.RequestAsync(access, new OwnerStayRequestCommand(body.PropertyId, body.StartDate, body.EndDate), cancellationToken);
        return StatusCode(StatusCodes.Status201Created, stay);
    }

    /// <summary>Withdraws an owner stay that has not ended; an approved one gives its block back. A repeat answers the same. Needs <c>stays.request</c>.</summary>
    [HttpPost("stays/{stayId:guid}/cancel")]
    [ProducesResponseType(typeof(OwnerStayView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OwnerStayView>> CancelStay(Guid stayId, CancellationToken cancellationToken)
    {
        if (await AccessAsync(cancellationToken) is not { } access)
            return Unauthorized();

        return Ok(await stays.CancelByOwnerAsync(access, stayId, cancellationToken));
    }

    private async Task<OwnerPortalAccess?> AccessAsync(CancellationToken cancellationToken) =>
        User.GetOwnerUserId(_customClaimNamespace) is { } ownerUserId
            ? await accessResolver.ResolveAsync(ownerUserId, cancellationToken)
            : null;
}

/// <summary>An owner stay to ask for: the property, the arrival and the departure (<c>yyyy-MM-dd</c>).</summary>
public sealed record OwnerStayRequestBody(Guid PropertyId, DateOnly StartDate, DateOnly EndDate);
