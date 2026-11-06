using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The state of the listing of a property on each channel, and the choice about a change made outside CasaZen (E02/F03/US01/T07, NF-09, NF-27, NF-38):
/// the host reads where each channel stands and why, what lacks and where to fix it, when the channel manager last confirmed the content, and decides
/// what to do when somebody changed it on Octorate.
/// </summary>
/// <remarks>
/// <para><b>Policy and reach.</b> <see cref="CasazenPolicies.PropertyRead"/> to read and <see cref="CasazenPolicies.PropertyWrite"/> to resolve a change,
/// each with the row-level check of the property (<see cref="PropertyOperations"/> on its <see cref="HostResource"/>, TN-3, AM-03): a property of another
/// org, or one the caller does not reach, answers 404 like one that does not exist, on all three endpoints. The summary narrows by the scope of the
/// caller (<c>InScope</c>), so a collaborator «Solo alcuni» sees only its properties.</para>
/// <para>Reading never writes to Octorate. Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/properties")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public sealed class ListingStatusController(
    IListingStatusService status,
    IOutsideChangeService outsideChanges,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IStringLocalizer<SharedResources> localizer) : ControllerBase
{
    /// <summary>The channels of a property with their state, completeness, missing steps, last confirmation, lag, reason and changes made outside CasaZen.</summary>
    /// <response code="200">The channels (empty while the property was never published).</response>
    /// <response code="404">The property is not the caller's, or the caller does not reach it.</response>
    [HttpGet("{id:guid}/channel-status")]
    [ProducesResponseType(typeof(ListingStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ListingStatusResponse>> GetStatus(Guid id, CancellationToken cancellationToken)
    {
        var resource = await hostResources.ForPropertyAsync(id, cancellationToken);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Read))
            return NotFound();

        var view = await status.GetAsync(id, cancellationToken);
        if (view is null)
            return NotFound();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(ListingStatusResponse.From(view, localizer));
    }

    /// <summary>The worst state of each property of the caller's scope that has a channel, for the list of the properties.</summary>
    /// <response code="200">One entry for each property with a channel.</response>
    /// <response code="403">The caller has no org.</response>
    [HttpGet("channel-status")]
    [ProducesResponseType(typeof(IReadOnlyList<ListingPropertySummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ListingPropertySummaryDto>>> GetSummary(CancellationToken cancellationToken)
    {
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");
        }

        var summaries = await status.GetSummaryAsync(scope, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(summaries.Select(ListingPropertySummaryDto.From).ToList());
    }

    /// <summary>
    /// The person's choice about a change made outside CasaZen on one channel: <c>keep</c> publishes CasaZen's version again over it, <c>take</c>
    /// adopts the descriptions the channel manager holds. Both store the new baseline, clear the change and close the «To do».
    /// </summary>
    /// <response code="204">Done (for <c>keep</c>: the republication is queued).</response>
    /// <response code="404">The property is not the caller's or not reached, or the channel is not one of the property.</response>
    /// <response code="409"><c>outside_change_not_found</c>: the channel has no change to resolve; <c>listing_modified_outside</c> and the other guards of a publication, for <c>keep</c>.</response>
    /// <response code="422"><c>outside_change_take_not_supported</c>: a group other than the descriptions changed (the groups are listed), keep CasaZen's version; <c>outside_change_text_too_long</c>.</response>
    [HttpPost("{id:guid}/channels/{code}/outside-change/resolve")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Resolve(
        Guid id,
        string code,
        [FromBody] ResolveOutsideChangeRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var resource = await hostResources.ForPropertyAsync(id, cancellationToken);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return NotFound();

        await outsideChanges.ResolveAsync(id, code, request.ToChoice(), userId, cancellationToken, User.GetRoles());
        return NoContent();
    }
}
