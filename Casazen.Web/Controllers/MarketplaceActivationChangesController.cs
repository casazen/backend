using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Casazen.Web.Controllers;

/// <summary>
/// The change of the settings of an activation and its deactivation (E09/F03/US01/T02, NF-58, MKT-05): the settings of a channel or a tool, the preview of what a deactivation
/// does, the deactivation itself and the change request of a function of the channel manager's license.
/// </summary>
/// <remarks>
/// <para><b>Policy.</b> <see cref="CasazenPolicies.OtaWrite"/> for all four. The org comes from the caller's membership and the scope from <see cref="IHostScopeResolver"/>, never
/// from the request: an activation of another org, or of a property outside the scope, answers 404, as one that does not exist.</para>
/// <para><b>Not behind the connector as a whole.</b> The partners never depend on Octorate, so the class carries no <c>Features:OctorateConnector</c> gate; the activations that go
/// through Octorate (a channel, a tool, a function of the license) answer the same 404 while the flag is off, decided by the service from the kind of the row, and the settings change
/// (a channel or a tool only) is gated here, before authentication.</para>
/// <para><b>No secret.</b> The portal username and password are request-only fields of the settings change: no answer carries them, the service stores none of them and logs none of
/// them. Every answer is <c>private, no-store</c>.</para>
/// </remarks>
[ApiController]
[Route("api/marketplace/activations/{id:guid}")]
[Authorize(Policy = CasazenPolicies.OtaWrite)]
public sealed class MarketplaceActivationChangesController(
    IMarketplaceChangeService changes,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver) : ControllerBase
{
    /// <summary>
    /// Changes the settings of a channel or a tool on the channel manager (<c>PATCH</c> of its connection): the identifier of the property on the channel and the login details the
    /// portal asks for. Only the members that are given are sent.
    /// </summary>
    /// <param name="id">The activation.</param>
    /// <param name="request">The new settings; at least one that the portal asks for.</param>
    /// <param name="cancellationToken">Cancels the request when the client goes away.</param>
    /// <response code="200">The activation.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409"><c>marketplace_octorate_not_connected</c>, <c>marketplace_connection_missing</c>.</response>
    /// <response code="422"><c>marketplace_change_not_supported</c> (a partner, a function, or a portal with nothing to change), <c>marketplace_settings_required</c> (nothing given to change), <c>marketplace_property_not_linked</c>.</response>
    /// <response code="502">The channel manager or the channel failed (<c>octorate_unavailable</c>, <c>octorate_partner_failed</c>, ...): nothing was changed.</response>
    [HttpPatch]
    [FeatureGate(FeatureFlags.OctorateConnector)]
    [ProducesResponseType(typeof(MarketplaceActivationChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceActivationChangeDto>> UpdateSettings(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] UpdateMarketplaceSettingsRequest? request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        // No body is a change that names nothing: 422, never a 400 that says nothing about the rule.
        NoStore();
        var outcome = await changes.UpdateSettingsAsync(
            caller.OrgId,
            caller.Scope,
            id,
            (request ?? new UpdateMarketplaceSettingsRequest()).ToChange(),
            caller.UserId,
            cancellationToken);
        return Ok(MarketplaceActivationChangeDto.From(outcome));
    }

    /// <summary>
    /// What a deactivation does, in plain sentences with the name put in. <c>requiresConfirmation</c> is always true and <c>guided</c> is true for a partner and for a function of the
    /// license. Nothing is asked of Octorate and nothing is written.
    /// </summary>
    /// <param name="id">The activation.</param>
    /// <param name="lang"><c>it</c> or <c>en</c>, for the sentences; the language of the request when absent.</param>
    /// <param name="cancellationToken">Cancels the read when the client goes away.</param>
    /// <response code="200">The preview.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    [HttpGet("deactivation-preview")]
    [ProducesResponseType(typeof(MarketplaceDeactivationPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarketplaceDeactivationPreviewDto>> PreviewDeactivation(
        Guid id, [FromQuery] string? lang, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var preview = await changes.PreviewDeactivationAsync(caller.OrgId, caller.Scope, id, LanguageOf(lang), cancellationToken);
        return Ok(MarketplaceDeactivationPreviewDto.From(preview));
    }

    /// <summary>
    /// Turns the activation off. <c>confirmed</c> must be true: without it (or without a body) the answer is 422 and nothing is deleted or changed. A channel or a tool: its connection is
    /// deleted on the channel manager and the row is kept as <c>Deactivated</c>. A partner: the host says it turned it off on the partner's site. A function of the license is never
    /// deactivated by CasaZen: 422 <c>marketplace_change_not_supported</c>, ask for a change instead.
    /// </summary>
    /// <param name="id">The activation.</param>
    /// <param name="request">The confirmation.</param>
    /// <param name="cancellationToken">Cancels the request when the client goes away.</param>
    /// <response code="200">The activation, now <c>Deactivated</c> (also when it was already).</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409"><c>marketplace_octorate_not_connected</c> (a channel or a tool needs the connection to the channel manager to delete its own).</response>
    /// <response code="422"><c>marketplace_confirmation_required</c>, <c>marketplace_change_not_supported</c>, <c>marketplace_property_not_linked</c>.</response>
    /// <response code="502">The channel manager or the channel failed (<c>octorate_unavailable</c>, <c>octorate_partner_failed</c>, ...): nothing was changed, try again.</response>
    [HttpPost("deactivate")]
    [ProducesResponseType(typeof(MarketplaceActivationChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceActivationChangeDto>> Deactivate(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] DeactivateMarketplaceActivationRequest? request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        // A request with no body has not said yes.
        NoStore();
        var outcome = await changes.DeactivateAsync(caller.OrgId, caller.Scope, id, request?.Confirmed == true, caller.UserId, cancellationToken);
        return Ok(MarketplaceActivationChangeDto.From(outcome));
    }

    /// <summary>
    /// Records that the host asked its channel manager for a change of a function of the license: <c>changeRequestedAt</c> is set and the status stays, until the license shows a different
    /// state. One request is open at a time. CasaZen asks the channel manager nothing and never writes the license.
    /// </summary>
    /// <param name="id">The activation.</param>
    /// <param name="request">The host's note (optional, at most 500 characters, not kept).</param>
    /// <param name="cancellationToken">Cancels the request when the client goes away.</param>
    /// <response code="200">The activation, with <c>changeRequestedAt</c>.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="404"><c>marketplace_activation_not_found</c>.</response>
    /// <response code="409"><c>marketplace_change_already_requested</c>.</response>
    /// <response code="422"><c>marketplace_change_not_supported</c> (not a function of the license), <c>marketplace_change_note_invalid</c>.</response>
    [HttpPost("change-request")]
    [ProducesResponseType(typeof(MarketplaceActivationChangeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<MarketplaceActivationChangeDto>> RequestChange(
        Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] MarketplaceFunctionChangeRequest? request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        NoStore();
        var outcome = await changes.RequestFunctionChangeAsync(caller.OrgId, caller.Scope, id, request?.Note, caller.UserId, cancellationToken);
        return Ok(MarketplaceActivationChangeDto.From(outcome));
    }

    /// <summary>The caller's id, org and scope; <c>null</c> when it has no org or reaches nothing in it.</summary>
    private async Task<Caller?> CallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId
            || await hostScopeResolver.ResolveHostScopeAsync(User, orgId, cancellationToken) is not { } scope)
        {
            return null;
        }

        return new Caller(userId, orgId, scope);
    }

    /// <summary><c>it</c> or <c>en</c>: the query when it names one, otherwise the language of the request.</summary>
    private static string LanguageOf(string? lang) =>
        MarketplaceLanguages.Normalize(string.IsNullOrWhiteSpace(lang) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : lang);

    private void NoStore() => Response.Headers.CacheControl = "private, no-store";

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private sealed record Caller(string UserId, Guid OrgId, HostScope Scope);
}
