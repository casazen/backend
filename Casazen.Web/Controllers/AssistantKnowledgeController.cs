using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Assistant;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// What the assistant may say (E05/F05/US01/T02, NF-66): the knowledge base of a property (the facts of its listing, its own entries and, under them, the organization's) and the
/// organization-wide entries. Behind <c>Features:MessageAssistant</c>: 404 while it is off, before authentication.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> The entries of a property are read with <see cref="CasazenPolicies.PropertyRead"/> and written with <see cref="CasazenPolicies.PropertyWrite"/>, <b>and the
/// row-level check of the property</b> (<see cref="PropertyOperations"/> on <see cref="Casazen.Core.Authorization.HostResource.ForProperty"/>, the check of the mapping of the listings): a
/// property of another organization, or one that a collaborator «Solo alcuni» was not given, answers 404 like one that does not exist, and an entry is always asked for through its own
/// property. The organization-wide entries are read with <see cref="CasazenPolicies.BookingRead"/> (they are shown under every property anyway) and written by the organization's
/// administrators only (<see cref="CasazenPolicies.OrgBillingAdmin"/>). Every answer is private and not cached: the text of an entry is the host's own and can hold a password.</para>
/// </remarks>
[ApiController]
[Route("api/assistant")]
[FeatureGate(FeatureFlags.MessageAssistant)]
[PrivateAnswer]
public sealed class AssistantKnowledgeController(
    IAssistantKnowledgeService knowledge,
    IPropertyService propertyService,
    IOrgContextResolver orgContextResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("properties/{propertyId:guid}/knowledge")]
    [Authorize(Policy = CasazenPolicies.PropertyRead)]
    [ProducesResponseType(typeof(AssistantKnowledgeViewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AssistantKnowledgeViewDto>> GetForProperty(
        Guid propertyId,
        [FromQuery] string? language,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Read))
            return PropertyNotFound();

        return Ok(AssistantKnowledgeViewDto.From(await knowledge.GetViewAsync(caller.OrgId, propertyId, language, cancellationToken)));
    }

    [HttpPost("properties/{propertyId:guid}/knowledge")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(KnowledgeEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<KnowledgeEntryDto>> CreateForProperty(
        Guid propertyId,
        [FromBody] SaveKnowledgeEntryRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        var created = await knowledge.CreateEntryAsync(caller.OrgId, propertyId, request.ToInput(), caller.UserId, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, KnowledgeEntryDto.From(created));
    }

    [HttpPut("properties/{propertyId:guid}/knowledge/{entryId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(KnowledgeEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<KnowledgeEntryDto>> UpdateForProperty(
        Guid propertyId,
        Guid entryId,
        [FromBody] SaveKnowledgeEntryRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        var updated = await knowledge.UpdateEntryAsync(caller.OrgId, propertyId, entryId, request.ToInput(), caller.UserId, cancellationToken);
        return Ok(KnowledgeEntryDto.From(updated));
    }

    [HttpDelete("properties/{propertyId:guid}/knowledge/{entryId:guid}")]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteForProperty(Guid propertyId, Guid entryId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await AssistantAccess.CanUsePropertyAsync(propertyService, authorizationService, User, caller.OrgId, propertyId, PropertyOperations.Write))
            return PropertyNotFound();

        await knowledge.DeleteEntryAsync(caller.OrgId, propertyId, entryId, cancellationToken);
        return NoContent();
    }

    [HttpGet("knowledge")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [ProducesResponseType(typeof(IReadOnlyList<KnowledgeEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<KnowledgeEntryDto>>> ListForOrganization(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var entries = await knowledge.ListOrganizationEntriesAsync(caller.OrgId, cancellationToken);
        return Ok(entries.Select(KnowledgeEntryDto.From).ToList());
    }

    [HttpPost("knowledge")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(KnowledgeEntryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<KnowledgeEntryDto>> CreateForOrganization([FromBody] SaveKnowledgeEntryRequest request, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var created = await knowledge.CreateEntryAsync(caller.OrgId, null, request.ToInput(), caller.UserId, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, KnowledgeEntryDto.From(created));
    }

    [HttpPut("knowledge/{entryId:guid}")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [RequestSizeLimit(16384)]
    [ProducesResponseType(typeof(KnowledgeEntryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<KnowledgeEntryDto>> UpdateForOrganization(
        Guid entryId,
        [FromBody] SaveKnowledgeEntryRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var updated = await knowledge.UpdateEntryAsync(caller.OrgId, null, entryId, request.ToInput(), caller.UserId, cancellationToken);
        return Ok(KnowledgeEntryDto.From(updated));
    }

    [HttpDelete("knowledge/{entryId:guid}")]
    [Authorize(Policy = CasazenPolicies.BookingRead)]
    [Authorize(Policy = CasazenPolicies.OrgBillingAdmin)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteForOrganization(Guid entryId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        await knowledge.DeleteEntryAsync(caller.OrgId, null, entryId, cancellationToken);
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
