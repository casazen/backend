using Casazen.Core.Authorization;
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
/// Who sends the police (Alloggiati Web) and ISTAT communications of a property (ADR-029, NF-42, CHK-03, E03/F01/US02/T01): read the
/// sender and its state, change it, test it and say that the channel manager is activated. One sender per property, never two automatic
/// ones: ISTAT always follows Alloggiati Web.
/// </summary>
/// <remarks>
/// <para><b>Senders offered in this release:</b> the channel manager (Octorate) and manual. «CasaZen sends» is not available: it cannot be
/// chosen (422 <c>sender_casazen_not_available</c>) and its connection test answers <c>NotAvailable</c>.</para>
/// <para><b>Authorization (TN-3).</b> <see cref="CasazenPolicies.PropertyRead"/> for the read and <see cref="CasazenPolicies.PropertyWrite"/> for every
/// other action, and the row-level check of the property on each: another org's property (or one that does not exist) answers 404
/// <c>property_not_found</c>, a property of the org the caller does not reach or may not write answers 403.</para>
/// <para>The answers are <c>private, no-store</c>: they say whether the property sends its police communications. The Questura credentials are
/// never returned, only whether they exist.</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/sender")]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
public class PropertySenderController(
    IPropertySenderService senderService,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<PropertySenderController> logger) : ControllerBase
{
    /// <summary>
    /// The sender of the property: who sends, ISTAT, the state (Active only when verified), how it is known, the police fields Octorate
    /// exposes, whether Questura credentials exist (never their value), whether the channel manager can be chosen and the delegation text to
    /// accept. For a channel-manager property it reads the police account of the accommodation on Octorate once, bounded in time; if that
    /// fails the fields are <c>null</c> and the answer is the same otherwise.
    /// </summary>
    /// <response code="200">The sender of the property.</response>
    /// <response code="403">The caller does not reach the property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PropertySenderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertySenderDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizePropertyAsync(propertyId, PropertyOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        var view = await senderService.GetAsync(propertyId, cancellationToken);
        return Answer(view);
    }

    /// <summary>
    /// Changes the sender of the property. ChannelManager or Manual; the host confirms that the sender in effect goes off, and for an automatic
    /// sender it accepts the delegation of the transmission (recorded on the organization before the sender is set). The change is audited
    /// once, by the store that writes it.
    /// </summary>
    /// <response code="200">The sender after the change.</response>
    /// <response code="403"><c>alloggiati_delegation_required</c>: an automatic sender needs the delegation accepted; or the caller may not write the property.</response>
    /// <response code="404"><c>property_not_found</c>.</response>
    /// <response code="409"><c>sender_switch_confirmation_required</c> (the problem carries <c>currentSender</c> and <c>newSender</c>); <c>sender_changed_concurrently</c>.</response>
    /// <response code="422"><c>sender_istat_mismatch</c>, <c>sender_not_applicable</c> (outside Italy), <c>sender_casazen_not_available</c>, <c>sender_channel_manager_not_available</c>.</response>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PropertySenderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertySenderDto>> Switch(
        Guid propertyId,
        [FromBody] SwitchSenderRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await senderService.SwitchAsync(
            propertyId,
            request.Sender!.Value,
            request.Confirmed,
            request.DelegationConsent,
            userId,
            cancellationToken,
            request.IstatSender);
        logger.LogInformation("User {UserId} set the sender of property {PropertyId} to {Sender}", userId, propertyId, view.Sender);
        return Answer(view);
    }

    /// <summary>
    /// Tests the sender. Channel manager: reads the police account on Octorate; <c>Verified</c> only with its proof, <c>Inactive</c> with
    /// guidance when Octorate says it does not send, <c>Unverifiable</c> when the API does not say. CasaZen: <c>NotAvailable</c>. Manual:
    /// <c>NotApplicable</c>. Nothing is transmitted or stored.
    /// </summary>
    /// <response code="200">The result of the test.</response>
    /// <response code="403">The caller may not write the property.</response>
    /// <response code="404"><c>property_not_found</c>.</response>
    [HttpPost("connection-test")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(SenderConnectionTestDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SenderConnectionTestDto>> TestConnection(Guid propertyId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        var result = await senderService.TestConnectionAsync(propertyId, cancellationToken);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(SenderConnectionTestDto.From(result, Guidance(result.Reason)));
    }

    /// <summary>
    /// The host says it saw «Activated» in the Octorate panel: stored with the date and the account as a declaration (state
    /// <c>Declared</c>, verification <c>UserDeclared</c>), never as verified. Only for the channel manager, once the delegation is accepted.
    /// </summary>
    /// <response code="200">The sender with the declaration.</response>
    /// <response code="403"><c>alloggiati_delegation_required</c>; or the caller may not write the property.</response>
    /// <response code="404"><c>property_not_found</c>.</response>
    /// <response code="409"><c>sender_changed_concurrently</c>.</response>
    /// <response code="422"><c>sender_declaration_not_applicable</c> (the sender is not the channel manager), <c>sender_not_applicable</c> (outside Italy).</response>
    [HttpPost("declare-activated")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(PropertySenderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertySenderDto>> DeclareActivated(Guid propertyId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizePropertyAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        var view = await senderService.DeclareActivatedAsync(propertyId, userId, cancellationToken);
        logger.LogInformation("User {UserId} declared the channel manager activated for property {PropertyId}", userId, propertyId);
        return Answer(view);
    }

    private ActionResult<PropertySenderDto> Answer(PropertySenderView view)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(PropertySenderDto.From(view, Guidance(view.Reason)));
    }

    /// <summary>
    /// The row-level check of the property (TN-3): 404 when it is not visible (another org, or none), 403 when it is the org's but the caller
    /// may not do <paramref name="operation"/> on it. <c>null</c> when allowed.
    /// </summary>
    private async Task<ActionResult?> AuthorizePropertyAsync(Guid propertyId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        // Tenant filter: another org's property is not found.
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the sender of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }

    /// <summary>What the host can do about a reason code, in the language of the request; <c>null</c> when there is nothing to say.</summary>
    private string? Guidance(string? reason) => reason switch
    {
        SenderReasonCodes.PoliceAccountInvalid => localizer["SenderGuidancePoliceAccountInvalid"].Value,
        SenderReasonCodes.PoliceAutorunOff => localizer["SenderGuidancePoliceAutorunOff"].Value,
        SenderReasonCodes.PoliceNotExposed => localizer["SenderGuidancePoliceNotExposed"].Value,
        SenderReasonCodes.NotConnected => localizer["SenderGuidanceNotConnected"].Value,
        SenderReasonCodes.Unavailable => localizer["SenderGuidanceUnavailable"].Value,
        SenderReasonCodes.CasaZenNotAvailable => localizer["SenderGuidanceCasaZenNotAvailable"].Value,
        _ => null,
    };
}
