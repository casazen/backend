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
/// The mapping of the listings found on the channel manager to the CasaZen properties, for the connect wizard (E02/F01/US02/T04,
/// ADR-023-R10, ADR-027-R8): list and refresh the listings, confirm one to a property, choose its room, reject it, import it as a new
/// property. Nothing is written to a property on Octorate until a person confirms its mapping.
/// </summary>
/// <remarks>
/// <para><b>Policies.</b> <see cref="CasazenPolicies.OtaRead"/> to read, <see cref="CasazenPolicies.OtaWrite"/> to change, and the row-level
/// check of the property the action is about, as <c>PropertiesController</c> does for the short-stay side of a property
/// (<see cref="PropertyOperations.Write"/> on <see cref="HostResource.ForProperty"/>, AM-03): a property of another org or one the caller
/// does not reach answers 404, the same answer as a property that does not exist. The import, which creates a property, also needs
/// <see cref="CasazenPolicies.PropertyWrite"/>. A listing with no property is an org-level decision: only a caller who reaches the whole
/// org rejects it.</para>
/// <para><b>Reach of the list.</b> A caller who reaches the whole org sees every listing; a collaborator «Solo alcuni» only the listings
/// of the properties it reaches (proposed, confirmed or among the candidates), with the other candidates left out.</para>
/// <para>Behind <c>Features:OctorateConnector</c>: 404 while it is off, before authentication.</para>
/// </remarks>
[ApiController]
[Route("api/octorate")]
[FeatureGate(FeatureFlags.OctorateConnector)]
[Authorize(Policy = CasazenPolicies.OtaRead)]
public sealed class OctorateMappingController(
    IChannelMappingService mappings,
    IPropertyService propertyService,
    IOrgContextResolver orgContextResolver,
    IHostScopeResolver hostScopeResolver,
    IAuthorizationService authorizationService) : ControllerBase
{
    /// <summary>The listings found on the channel manager with their mapping, as stored (no call to Octorate).</summary>
    /// <response code="200">The listings the caller reaches.</response>
    /// <response code="403">The caller has no org.</response>
    [HttpGet("properties")]
    [ProducesResponseType(typeof(IReadOnlyList<OctorateListingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<OctorateListingDto>>> List(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(await VisibleAsync(await mappings.ListAsync(caller.OrgId, cancellationToken), caller.Scope));
    }

    /// <summary>Reads the listings on Octorate again and proposes a property for each; confirmed and rejected rows are not touched.</summary>
    /// <response code="200">The listings the caller reaches, after the refresh.</response>
    /// <response code="403">The caller has no org.</response>
    /// <response code="409"><c>octorate_not_connected</c>: the org is not connected to Octorate.</response>
    [HttpPost("properties/refresh")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(typeof(IReadOnlyList<OctorateListingDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<IReadOnlyList<OctorateListingDto>>> Refresh(CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        Response.Headers.CacheControl = "private, no-store";
        return Ok(await VisibleAsync(await mappings.RefreshAsync(caller.OrgId, cancellationToken), caller.Scope));
    }

    /// <summary>Confirms the listing to the property of the body.</summary>
    /// <response code="204">Confirmed.</response>
    /// <response code="404">The listing is not the org's, or the property is not in the org or not reachable by the caller.</response>
    /// <response code="409"><c>octorate_mapping_property_taken</c>: the property is confirmed to another listing.</response>
    /// <response code="422"><c>octorate_room_invalid</c>: the room given is not a room of the listing.</response>
    [HttpPut("mapping/{octorateAccommodationId}")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Confirm(
        string octorateAccommodationId,
        [FromBody] ConfirmMappingRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (request.PropertyId is not { } propertyId || !await CanWritePropertyAsync(caller, propertyId))
            return PropertyNotFound();

        await mappings.ConfirmAsync(caller.OrgId, octorateAccommodationId, propertyId, request.OctorateProductId, caller.UserId, cancellationToken);
        return NoContent();
    }

    /// <summary>Chooses the Octorate room of a confirmed listing with several rooms.</summary>
    /// <response code="204">Set.</response>
    /// <response code="404">The listing is not the org's, or its property is not reachable by the caller.</response>
    /// <response code="422"><c>octorate_room_invalid</c>: not a room of the listing; <c>octorate_mapping_not_confirmed</c>: confirm it first.</response>
    [HttpPut("mapping/{octorateAccommodationId}/room")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetRoom(
        string octorateAccommodationId,
        [FromBody] SetRoomRequest request,
        CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await CanDecideAsync(caller, await RowAsync(caller, octorateAccommodationId, cancellationToken)))
            return MappingNotFound();

        await mappings.SetRoomAsync(caller.OrgId, octorateAccommodationId, request.OctorateProductId, caller.UserId, cancellationToken);
        return NoContent();
    }

    /// <summary>Rejects the mapping of the listing: it is not one of the org's CasaZen properties.</summary>
    /// <response code="204">Rejected.</response>
    /// <response code="404">The listing is not the org's, or the caller does not reach its property (or the whole org, for a listing with no property).</response>
    [HttpDelete("mapping/{octorateAccommodationId}")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reject(string octorateAccommodationId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        if (!await CanDecideAsync(caller, await RowAsync(caller, octorateAccommodationId, cancellationToken)))
            return MappingNotFound();

        await mappings.RejectAsync(caller.OrgId, octorateAccommodationId, caller.UserId, cancellationToken);
        return NoContent();
    }

    /// <summary>Imports the listing as a new CasaZen property, inactive and to check, and confirms the mapping. Nothing is created on Octorate.</summary>
    /// <response code="201">The new property.</response>
    /// <response code="404">The listing is not the org's.</response>
    /// <response code="409"><c>plan_limit_reached</c>: the org has as many properties as its plan allows; <c>octorate_mapping_already_confirmed</c>.</response>
    [HttpPost("mapping/{octorateAccommodationId}/import")]
    [Authorize(Policy = CasazenPolicies.OtaWrite)]
    [Authorize(Policy = CasazenPolicies.PropertyWrite)]
    [ProducesResponseType(typeof(ImportListingResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ImportListingResponse>> Import(string octorateAccommodationId, CancellationToken cancellationToken)
    {
        if (await CallerAsync(cancellationToken) is not { } caller)
            return NoOrg();

        var propertyId = await mappings.ImportAsPropertyAsync(caller.OrgId, octorateAccommodationId, caller.UserId, cancellationToken);
        return Created($"/api/properties/{propertyId}", new ImportListingResponse(propertyId));
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

    /// <summary>The row-level check of <c>PropertiesController</c>: the property is in the caller's org and the caller may write it.</summary>
    private async Task<bool> CanWritePropertyAsync(Caller caller, Guid propertyId)
    {
        var property = await propertyService.GetPropertyRecordAsync(propertyId);
        return property is not null
               && property.OrgId == caller.OrgId
               && await authorizationService.IsAuthorizedAsync(User, HostResource.ForProperty(property), PropertyOperations.Write);
    }

    /// <summary>
    /// Whether the caller may decide on a listing: the row-level check of its property (proposed or confirmed), or, for a listing with
    /// no property, a scope over the whole org (an org-level decision).
    /// </summary>
    private async Task<bool> CanDecideAsync(Caller caller, ChannelMappingRow? row) => row switch
    {
        null => false,
        { Property: { } property } => await CanWritePropertyAsync(caller, property.Id),
        _ => caller.Scope.IsOrgWide,
    };

    private async Task<ChannelMappingRow?> RowAsync(Caller caller, string octorateAccommodationId, CancellationToken cancellationToken) =>
        (await mappings.ListAsync(caller.OrgId, cancellationToken))
        .FirstOrDefault(row => string.Equals(row.OctorateAccommodationId, octorateAccommodationId?.Trim(), StringComparison.Ordinal));

    /// <summary>The listings the caller reaches: all of them for the whole org, else those of the properties of its scope.</summary>
    private async Task<IReadOnlyList<OctorateListingDto>> VisibleAsync(IReadOnlyList<ChannelMappingRow> rows, HostScope scope)
    {
        if (scope.IsOrgWide)
            return [.. rows.Select(row => OctorateListingDto.From(row, _ => true))];

        var reached = (await propertyService.GetPropertiesAsync(scope)).Select(property => property.Id).ToHashSet();
        return
        [
            .. rows
                .Where(row => row.Property is { } property
                    ? reached.Contains(property.Id)
                    : row.Candidates.Any(candidate => reached.Contains(candidate.Id)))
                .Select(row => OctorateListingDto.From(row, reached.Contains)),
        ];
    }

    private ObjectResult NoOrg() => this.ApiProblem(StatusCodes.Status403Forbidden, "no_org_context", "NoOrganizationContext");

    private ObjectResult PropertyNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, "octorate_property_not_found", "OctoratePropertyNotFound");

    private ObjectResult MappingNotFound() =>
        this.ApiProblem(StatusCodes.Status404NotFound, "octorate_mapping_not_found", "OctorateMappingNotFound");

    private sealed record Caller(string UserId, Guid OrgId, HostScope Scope);
}
