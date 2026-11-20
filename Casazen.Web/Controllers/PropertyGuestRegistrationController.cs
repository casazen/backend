using Casazen.Core.Authorization;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Compliance;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace Casazen.Web.Controllers;

/// <summary>
/// The regime of the registration of the guests of a property and the host's setting (E29/F02/US01/T01, NF-125, decisions D3, D5 and D12): what the place of the
/// property asks (obligation, channel, deadline, retention, sources and how certain they are), what CasaZen does, and whether the duty is treated as required,
/// not required or left to the host. It reads the catalog and sends nothing to anybody.
/// </summary>
/// <remarks>
/// <para><b>Behind <c>Features:GuestRegistrationRegimes</c></b> (off by default): off, both actions answer 404 <c>not_found</c> before authentication, nothing reads the
/// setting of the host and Ticino behaves as before.</para>
/// <para><b>Authorization (TN-3).</b> <see cref="CasazenPolicies.PropertyRead"/> for the read and <see cref="CasazenPolicies.PropertyWrite"/> for the setting (the
/// same permissions as the other settings of a property, for instance its sender), and the row-level check of the property on each: another org's property (or one that
/// does not exist) answers 404 <c>property_not_found</c>, a property of the org the caller does not reach (a collaborator «Solo alcuni» not given it) or may not write
/// answers 403, and nothing is read or stored for either.</para>
/// <para>The answers are private and never cached. They hold no guest and no credential: only the catalog, the place of the property and the host's own setting.</para>
/// <para><b>Errors</b> are ProblemDetails with a stable <c>code</c>: 400 <c>validation_error</c> (the body has no <c>override</c>, or a value that is not one of the
/// settings); 401; 403; 404 <c>property_not_found</c>; 422 <c>guest_registration_override_not_applicable</c> (the property is not in Switzerland: the Italian duty is not the
/// host's to waive) and <c>guest_registration_country_not_supported</c> (the catalog has no regime for the country of the property).</para>
/// </remarks>
[ApiController]
[Route("api/properties/{propertyId:guid}/guest-registration")]
[FeatureGate(FeatureFlags.GuestRegistrationRegimes)]
[Authorize(Policy = CasazenPolicies.PropertyRead)]
[PrivateAnswer]
public sealed class PropertyGuestRegistrationController(
    IGuestRegistrationRegimeResolver regimes,
    IGuestNotificationOverrideService overrides,
    IHostResourceLookup hostResources,
    IAuthorizationService authorizationService,
    IStringLocalizer<SharedResources> localizer,
    ILogger<PropertyGuestRegistrationController> logger) : ControllerBase
{
    /// <summary>The regime of the property, the duty in effect, the host's setting, the place and what the property still lacks.</summary>
    /// <response code="200">The card of the property.</response>
    /// <response code="403">The caller does not reach the property.</response>
    /// <response code="404"><c>property_not_found</c>: no such property in the caller's org.</response>
    /// <response code="422"><c>guest_registration_country_not_supported</c>.</response>
    [HttpGet]
    [ProducesResponseType(typeof(GuestRegistrationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<GuestRegistrationDto>> Get(Guid propertyId, CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, PropertyOperations.Read, cancellationToken);
        if (denied is not null)
            return denied;

        return await AnswerAsync(propertyId, cancellationToken);
    }

    /// <summary>
    /// Saves the host's setting: <c>Required</c> (raise the duty), <c>NotRequired</c> (do not) or <c>null</c> (follow the regime again). Reversible, never blocking;
    /// saving what is already saved changes nothing. Answers the card as it is after the change.
    /// </summary>
    /// <response code="200">The card of the property after the change.</response>
    /// <response code="400"><c>validation_error</c>: no <c>override</c> in the body, or an unknown value.</response>
    /// <response code="403">The caller may not write the property.</response>
    /// <response code="404"><c>property_not_found</c>.</response>
    /// <response code="422"><c>guest_registration_override_not_applicable</c>: the property is not in Switzerland.</response>
    [HttpPut]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(GuestRegistrationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<GuestRegistrationDto>> Put(
        Guid propertyId,
        [FromBody] UpdateGuestRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await AuthorizeAsync(propertyId, PropertyOperations.Write, cancellationToken);
        if (denied is not null)
            return denied;

        if (!request.OverrideSent)
        {
            // A body without the member is a mistake, never «follow the regime again» by omission: that is said with a null.
            ModelState.AddModelError("override", localizer[GuestRegistrationErrorCodes.OverrideRequiredMessageKey]);
            return ValidationProblem(ModelState);
        }

        if (User.GetUserId() is not { Length: > 0 } userId)
            return Unauthorized();

        await overrides.SetAsync(propertyId, request.Override, userId, cancellationToken);
        return await AnswerAsync(propertyId, cancellationToken);
    }

    private async Task<ActionResult<GuestRegistrationDto>> AnswerAsync(Guid propertyId, CancellationToken cancellationToken)
    {
        var resolution = await regimes.ResolveAsync(propertyId, cancellationToken);
        if (resolution.Regime is null)
        {
            return this.ApiProblem(
                StatusCodes.Status422UnprocessableEntity,
                GuestRegistrationErrorCodes.CountryNotSupported,
                GuestRegistrationErrorCodes.CountryNotSupportedMessageKey);
        }

        return Ok(GuestRegistrationDto.From(resolution, localizer));
    }

    /// <summary>
    /// The row-level check of the property (TN-3): 404 when it is not visible (another org, or none), 403 when it is the org's but the caller may not do
    /// <paramref name="operation"/> on it. <c>null</c> when allowed.
    /// </summary>
    private async Task<ActionResult?> AuthorizeAsync(Guid propertyId, HostOperationRequirement operation, CancellationToken cancellationToken)
    {
        // Tenant filter: another org's property is not found, the same answer as a property that does not exist.
        var resource = await hostResources.ForPropertyAsync(propertyId, cancellationToken);
        if (resource is null)
            return this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

        if (!await authorizationService.IsAuthorizedAsync(User, resource, operation))
        {
            logger.LogWarning(
                "User {UserId} denied {Permission} on the guest registration of property {PropertyId}",
                User.GetUserId(), operation.PermissionKey, propertyId);
            return Forbid();
        }

        return null;
    }
}
