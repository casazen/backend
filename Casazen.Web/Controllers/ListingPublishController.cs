using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The publication of a property to Octorate and to the channels that receive content (E02/F03/US01/T02, NF-27, ADR-027): the host asks for it from
/// the listing wizard; the work runs in a job and the wizard polls <c>GET api/properties/{id}/channel-status</c> (E02/F03/US01/T07).
/// </summary>
/// <remarks>
/// <para><b>Policy and reach.</b> <see cref="CasazenPolicies.PropertyWrite"/>, and the row-level check of the property
/// (<see cref="PropertyOperations.Write"/> on its <see cref="HostResource"/>, TN-3, AM-03): a property of another org, or one the caller does not
/// reach (a collaborator «Solo alcuni»), answers 404 like a property that does not exist. The service checks the reach again with the roles of the
/// token, so a job queued for a person never publishes more than the person reaches.</para>
/// <para>Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication. Nothing of the listing is logged.</para>
/// </remarks>
[ApiController]
[Route("api/properties/{id:guid}/publish")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.PropertyWrite)]
public sealed class ListingPublishController(
    IContentPushService push,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>
    /// Queues the publication of the property to the channels of the body. It answers 202 with the channel rows now <c>Sending</c>; a channel whose
    /// requirements are not met, or that fails, becomes <c>ToFix</c> with a reason, and the others go on.
    /// </summary>
    /// <response code="202">Queued: the channels asked for, now <c>Sending</c>.</response>
    /// <response code="404">The property is not the caller's, or the caller does not reach it.</response>
    /// <response code="409"><c>octorate_mapping_unconfirmed</c>: the property sits in a proposed or ambiguous mapping; <c>octorate_existing_inventory_must_import</c>: the property is not declared never published and has no mapping; <c>listing_modified_outside</c>: a requested channel was changed outside CasaZen (the channels are listed), choose first or send <c>overwrite</c>.</response>
    /// <response code="422"><c>listing_content_incomplete</c>: the Italian or English text is missing (the missing ones are listed); <c>listing_channels_required</c>; <c>listing_channel_unknown</c>.</response>
    [HttpPost]
    [ProducesResponseType(typeof(ListingPublishResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<ListingPublishResponse>> Publish(
        Guid id,
        [FromBody] ListingPublishRequest request,
        CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var resource = await hostResources.ForPropertyAsync(id, cancellationToken);
        if (resource is null || !await authorizationService.IsAuthorizedAsync(User, resource, PropertyOperations.Write))
            return NotFound();

        var channels = await push.PublishAsync(id, request.Channels ?? [], request.Overwrite, userId, cancellationToken, User.GetRoles());

        Response.Headers.CacheControl = "private, no-store";
        return Accepted(new ListingPublishResponse(id, [.. channels.Select(ListingPublishChannelDto.From)]));
    }
}
