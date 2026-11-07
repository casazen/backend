using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The endpoints behind the activation wizard of a sales channel or a connected tool through the channel manager's API (E09/F02/US01/T02, NF-56,
/// MKT-03): the plan, the start, the state, the import, the mapping and the activation. Behind <c>Features:OctorateConnector</c>: 404 while it is off,
/// before authentication.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.OtaRead"/> for the reads, <see cref="CasazenPolicies.OtaWrite"/> for the actions. The org comes from
/// the caller's membership and the scope from <see cref="IHostScopeResolver"/>, never from the request: a collaborator limited to some properties
/// reaches the activations of those only, and a property or an activation outside the scope answers 404, as one that does not exist.</para>
/// <para><b>No secret.</b> The portal username and password are request-only fields of the start: no answer carries them, the service stores none of
/// them and logs none of them. Every answer is <c>private, no-store</c>. The plan carries no address of the channel manager's panel: the access
/// there is the guided step <c>channelAccess</c> of the activation epic.</para>
/// </remarks>
[ApiController]
[Route("api/marketplace/activations")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.OtaRead)]
public sealed class MarketplaceActivationsController(
    IMarketplaceChannelActivationService activations,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>What the portal asks of the person and the link state of the property.</summary>
    /// <param name="kind"><c>Channel</c> or <c>Tool</c> (the name, case is ignored).</param>
    /// <param name="code">The code of the portal in the catalog.</param>
    /// <param name="propertyId">The property to connect.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The plan.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_portal_unknown</c>, or <c>marketplace_property_not_found</c> for a property outside the caller's scope.</response>
    /// <response code="409"><c>marketplace_octorate_not_connected</c>.</response>
    [HttpGet("plan")]
    [ProducesResponseType(typeof(MarketplaceActivationPlanDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MarketplaceActivationPlanDto>> GetPlan(
        [FromQuery] string? kind, [FromQuery] string? code, [FromQuery] Guid propertyId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!TryParseKind(kind, out var parsedKind))
            return this.ApiProblem(StatusCodes.Status404NotFound, "marketplace_portal_unknown", "MarketplacePortalUnknown");

        NoStore();
        var plan = await activations.GetPlanAsync(caller.OrgId, caller.Scope, parsedKind, code ?? string.Empty, propertyId, cancellationToken);
        return Ok(MarketplaceActivationPlanDto.From(plan));
    }

    /// <summary>
    /// Creates the connection on the channel manager and the activation (Pending). Starting again returns the activation that exists and creates
    /// nothing.
    /// </summary>
    /// <response code="200">The state of the activation.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_portal_unknown</c>, <c>marketplace_property_not_found</c>.</response>
    /// <response code="409"><c>marketplace_octorate_not_connected</c>, <c>marketplace_connection_exists</c>; <c>octorate_conflict</c> from the channel manager.</response>
    /// <response code="422"><c>marketplace_property_not_linked</c>, <c>marketplace_portal_not_creatable</c>, <c>marketplace_credentials_required</c>.</response>
    /// <response code="502">The channel manager or the channel failed (<c>octorate_unavailable</c>, <c>octorate_partner_failed</c>, ...).</response>
    [HttpPost]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplaceActivationStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceActivationStateDto>> Start(
        [FromBody] StartMarketplaceActivationRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var state = await activations.StartAsync(caller.OrgId, caller.Scope, request.ToStart(), cancellationToken);
        return Ok(MarketplaceActivationStateDto.From(state));
    }

    /// <summary>The state of the activation, read from the channel manager: the status, whether the access on the panel is done, the last sync and the last error code.</summary>
    /// <response code="200">The state.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(MarketplaceActivationStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplaceActivationStateDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        return Ok(MarketplaceActivationStateDto.From(await activations.GetStateAsync(caller.OrgId, caller.Scope, id, cancellationToken)));
    }

    /// <summary>Imports the rooms, the reservations and the calendar of the channel into the channel manager (asynchronous there) and answers the state.</summary>
    /// <response code="200">The state.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409">
    /// <c>d6_operation_unverified</c> (an operation of the D6 sequence is not verified for the channel: it is not called), <c>marketplace_connection_missing</c>
    /// (the connection is no longer on the channel manager: the activation is freed, start it again), <c>marketplace_panel_access_pending</c>.
    /// </response>
    [HttpPost("{id:guid}/import")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplaceActivationStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MarketplaceActivationStateDto>> Import(Guid id, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        return Ok(MarketplaceActivationStateDto.From(await activations.ImportAsync(caller.OrgId, caller.Scope, id, cancellationToken)));
    }

    /// <summary>The rooms of the listing on the channel with the proposals of CasaZen, and the products of the property's listing.</summary>
    /// <response code="200">The mapping.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409"><c>marketplace_connection_missing</c>.</response>
    [HttpGet("{id:guid}/mapping")]
    [ProducesResponseType(typeof(MarketplaceActivationMappingDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<MarketplaceActivationMappingDto>> GetMapping(Guid id, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        return Ok(MarketplaceActivationMappingDto.From(await activations.GetMappingAsync(caller.OrgId, caller.Scope, id, cancellationToken)));
    }

    /// <summary>Saves the mapping the person chose and completes it on the channel manager.</summary>
    /// <response code="200">The state.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409"><c>marketplace_connection_missing</c>.</response>
    /// <response code="422"><c>marketplace_mapping_required</c> (an empty or missing list), <c>marketplace_mapping_invalid</c>.</response>
    [HttpPut("{id:guid}/mapping")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplaceActivationStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceActivationStateDto>> SaveMapping(
        Guid id, [FromBody] SaveMarketplaceMappingRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        IReadOnlyList<MarketplaceRoomChoice> choices =
            [.. (request.Mappings ?? []).Select(choice => new MarketplaceRoomChoice(choice.ChannelRoomId, choice.ProductId))];
        NoStore();
        return Ok(MarketplaceActivationStateDto.From(await activations.SaveMappingAsync(caller.OrgId, caller.Scope, id, choices, cancellationToken)));
    }

    /// <summary>
    /// Marks the connection connected and verifies it: Active only when the channel manager shows it working, otherwise To fix with the error code.
    /// Needs the D6 flag of «mark connected» for the channel (fail closed).
    /// </summary>
    /// <response code="200">The state.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409">
    /// <c>d6_operation_unverified</c> («mark connected» is not verified for the channel: it is not called, nor are the pulls switched on),
    /// <c>marketplace_connection_missing</c>, <c>marketplace_panel_access_pending</c>.
    /// </response>
    /// <response code="422"><c>marketplace_mapping_required</c>: no room is mapped yet.</response>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(MarketplaceActivationStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceActivationStateDto>> Activate(Guid id, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        return Ok(MarketplaceActivationStateDto.From(await activations.ActivateAsync(caller.OrgId, caller.Scope, id, cancellationToken)));
    }

    /// <summary>The caller's org and scope; <c>null</c> when it has no org or reaches nothing in it.</summary>
    private async Task<Caller?> CallerAsync(CancellationToken cancellationToken)
    {
        if (await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return null;
        }

        return new Caller(orgId, scope);
    }

    /// <summary>The kind named by its name (<c>channel</c>, <c>Tool</c>...), never by its number, and only a channel or a tool.</summary>
    private static bool TryParseKind(string? kind, out MarketplaceActivationKind parsed)
    {
        parsed = default;
        return kind is not null
               && Enum.TryParse(kind, ignoreCase: true, out parsed)
               && parsed is MarketplaceActivationKind.Channel or MarketplaceActivationKind.Tool
               && !int.TryParse(kind, out _);
    }

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private sealed record Caller(Guid OrgId, HostScope Scope);
}
