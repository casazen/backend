using Casazen.Core.Assistant;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the assistant does in each situation (E05/F05/US01/T03, NF-66, NF-67): the 15 situations of <see cref="AssistantSituationCatalog"/> with their mode, the text of the answer or the
/// topic it comes from, and the start of the trust period, for the organization and as exceptions of a property. Behind <c>Features:MessageAssistant</c>: 404 while it is off, before
/// authentication. The route names a situation by its stable code (<c>check_in_time</c>) or by the name of its enum member, in any case; anything else is 404 <c>assistant_situation_not_found</c>.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> The situations of the organization are read with <see cref="CasazenPolicies.BookingRead"/> and saved only by the organization's administrators
/// (<see cref="CasazenPolicies.OrgBillingAdmin"/>). The exceptions of a property are read with <see cref="CasazenPolicies.PropertyRead"/> and saved or removed with
/// <see cref="CasazenPolicies.PropertyWrite"/>, <b>and the row-level check of the property</b> (<see cref="PropertyOperations"/>, the check of the mapping of the listings): a property of
/// another organization, or one that a collaborator «Solo alcuni» was not given, answers 404 like one that does not exist.</para>
/// <para><b>The templates</b> are the host's own text and are shown only to the caller who may edit them: the administrators of the organization for the rows of the organization (on its own
/// list and on the list of a property, where such a row is the value a property inherits), a caller who may write the property for the exception of that property. Everyone else, a caller
/// who has just <c>property.write</c> on the property included, reads <c>hasTemplate</c> for a row of the organization. Every answer is private and not cached.</para>
/// <para><b>The rule of the modes.</b> A sensitive situation, and <c>Other</c>, is saved as escalate or draft only: 422 <c>assistant_mode_not_allowed</c>, whoever asks.</para>
/// </remarks>
[ApiController]
[Route("api/assistant")]
[FeatureGate(FeatureFlags.MessageAssistant)]
[PrivateAnswer]
public sealed class AssistantSituationsController(
    IAssistantSituationService situations,
    IPropertyService propertyService,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("situations")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(IReadOnlyList<EffectiveSituationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<EffectiveSituationDto>>> ListForOrganization(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var canEdit = (await authorizationService.AuthorizeAsync(User, CasazenPolicies.OrgBillingAdmin)).Succeeded;
        var items = await situations.ListEffectiveAsync(caller.OrgId, null, cancellationToken);
        return Ok(items.Select(item => EffectiveSituationDto.From(item, canEdit)).ToList());
    }

    [HttpPut("situations/{situation}")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(EffectiveSituationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<EffectiveSituationDto>> SaveForOrganization(
        string situation,
        [FromBody] SaveAssistantSituationRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!AssistantSituationCatalog.TryParse(situation, out var parsed))
            return SituationNotFound();

        var saved = await situations.SaveAsync(
            caller.OrgId, null, parsed, request.Mode ?? AssistantSituationCatalog.Of(parsed).DefaultMode, request.Template, request.Source, caller.UserId, cancellationToken);
        return Ok(EffectiveSituationDto.From(saved, includeTemplate: true));
    }

    [HttpGet("properties/{propertyId:guid}/situations")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(IReadOnlyList<EffectiveSituationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<EffectiveSituationDto>>> ListForProperty(Guid propertyId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Read))
            return PropertyNotFound();

        var canEdit = await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write);
        var canReadOrganizationTemplates = canEdit && (await authorizationService.AuthorizeAsync(User, CasazenPolicies.OrgBillingAdmin)).Succeeded;
        var items = await situations.ListEffectiveAsync(caller.OrgId, propertyId, cancellationToken);
        return Ok(items.Select(item => EffectiveSituationDto.From(item, item.Origin == AssistantSettingOrigin.Organization ? canReadOrganizationTemplates : canEdit)).ToList());
    }

    [HttpPut("properties/{propertyId:guid}/situations/{situation}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(EffectiveSituationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<EffectiveSituationDto>> SaveForProperty(
        Guid propertyId,
        string situation,
        [FromBody] SaveAssistantSituationRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        if (!AssistantSituationCatalog.TryParse(situation, out var parsed))
            return SituationNotFound();

        var saved = await situations.SaveAsync(
            caller.OrgId, propertyId, parsed, request.Mode ?? AssistantSituationCatalog.Of(parsed).DefaultMode, request.Template, request.Source, caller.UserId, cancellationToken);
        return Ok(EffectiveSituationDto.From(saved, includeTemplate: true));
    }

    [HttpDelete("properties/{propertyId:guid}/situations/{situation}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteForProperty(Guid propertyId, string situation, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        if (!AssistantSituationCatalog.TryParse(situation, out var parsed))
            return SituationNotFound();

        await situations.DeleteExceptionAsync(caller.OrgId, propertyId, parsed, cancellationToken);
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

    private ObjectResult SituationNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, "assistant_situation_not_found", "AssistantSituationNotFound");

    private sealed record Caller(string UserId, Guid OrgId);
}
