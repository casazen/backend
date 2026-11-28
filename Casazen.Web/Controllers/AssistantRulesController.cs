using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// How the assistant writes (E05/F05/US01/T02, NF-66): the rules of the organization (tone, languages, signature, hours, allowed and forbidden topics, the confidence threshold and the
/// organization's switch) and the exceptions of a property, which fall back to the organization's value when a field is not given. Behind <c>Features:MessageAssistant</c>: 404 while it
/// is off, before authentication.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> The rules of the organization are read with <see cref="CasazenPolicies.BookingRead"/> (a collaborator needs them to see what its property follows) and written
/// only by the organization's administrators (<see cref="CasazenPolicies.OrgBillingAdmin"/>: an organization-wide configuration, like the branding and the domain). The exceptions of a
/// property are read with <see cref="CasazenPolicies.PropertyRead"/> and written with <see cref="CasazenPolicies.PropertyWrite"/>, <b>and the row-level check of the property</b>
/// (<see cref="PropertyOperations"/>, the check of the mapping of the listings): a property of another organization, or one that a collaborator «Solo alcuni» was not given, answers 404
/// like one that does not exist. Every answer is private and not cached.</para>
/// </remarks>
[ApiController]
[Route("api/assistant")]
[FeatureGate(FeatureFlags.MessageAssistant)]
[PrivateAnswer]
public sealed class AssistantRulesController(
    IAssistantRulesService rules,
    IPropertyService propertyService,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("rules")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(AssistantRulesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AssistantRulesDto>> Get(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        return Ok(AssistantRulesDto.From(await rules.GetAsync(caller.OrgId, cancellationToken)));
    }

    [HttpPut("rules")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(AssistantRulesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<AssistantRulesDto>> Save([FromBody] SaveAssistantRulesRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var saved = await rules.SaveAsync(caller.OrgId, request.ToInput(), caller.UserId, cancellationToken);
        return Ok(AssistantRulesDto.From(saved));
    }

    [HttpGet("properties/{propertyId:guid}/settings")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(PropertyAssistantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PropertyAssistantSettingsDto>> GetForProperty(Guid propertyId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Read))
            return PropertyNotFound();

        return Ok(PropertyAssistantSettingsDto.From(await rules.GetPropertySettingsAsync(caller.OrgId, propertyId, cancellationToken)));
    }

    [HttpPut("properties/{propertyId:guid}/settings")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(PropertyAssistantSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<PropertyAssistantSettingsDto>> SaveForProperty(
        Guid propertyId,
        [FromBody] SavePropertyAssistantSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        var saved = await rules.SavePropertySettingsAsync(caller.OrgId, propertyId, request.ToInput(), caller.UserId, cancellationToken);
        return Ok(PropertyAssistantSettingsDto.From(saved));
    }

    [HttpDelete("properties/{propertyId:guid}/settings")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteForProperty(Guid propertyId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        await rules.DeletePropertySettingsAsync(caller.OrgId, propertyId, cancellationToken);
        return NoContent();
    }

    private async Task<Caller?> CallerAsync(CancellationToken cancellationToken)
    {
        if (User.GetUserId() is not { Length: > 0 } userId
            || await orgContextResolver.GetOrProvisionOrgIdAsync(cancellationToken) is not { } orgId)
        {
            return null;
        }

        return new Caller(userId, orgId);
    }

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private ObjectResult PropertyNotFound() => this.ApiProblem(StatusCodes.Status404NotFound, "property_not_found", "PropertyNotFound");

    private sealed record Caller(string UserId, Guid OrgId);
}
