using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The people of the caller's org (AM-02): list them with the seats, change a role, deactivate, reactivate and remove. For
/// the owner and the administrators (policy <see cref="CasazenPolicies.OrgMembersManage"/>); an administrator is in the
/// hands of the owner only, the owner is never touched (<c>org_last_owner</c>) and nobody becomes owner. Behind the
/// <c>OrgTeam</c> flag: 404 while it is off. <c>{id}</c> is the member row (<c>OrgMemberDto.Id</c>), not the account id.
/// Since AM-03 also the properties a member reaches (<c>{id}/properties</c>). Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
[ApiController]
[Route("api/orgs/me/members")]
[FeatureGate(FeatureFlags.OrgTeam)]
[Authorize(Policy = CasazenPolicies.OrgMembersManage)]
public class OrgMembersController(
    IOrgTeamService team,
    IOrgPropertyAccessService propertyAccess,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    /// <summary>
    /// The properties of the org with, for this member, whether it reaches each one, and for each property how many active
    /// people do («chi può accedere»). <c>scopeSupported</c> is false for a member who is not a collaborator (it reaches
    /// every property). 404 <c>org_member_not_found</c>.
    /// </summary>
    [HttpGet("{id:guid}/properties")]
    [ProducesResponseType(typeof(OrgMemberPropertiesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrgMemberPropertiesDto>> GetProperties(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        return Ok(OrgMemberPropertiesDto.From(await propertyAccess.GetAsync(caller.OrgId, id, cancellationToken)));
    }

    /// <summary>
    /// Sets the properties a member reaches: <c>All</c> (every property, the ones added later too) or <c>Selected</c>
    /// («Solo alcuni») with exactly the ids given, none meaning it sees nothing. Only a collaborator can be limited; the
    /// change is seen at once by this API instance and within the authorization cache duration (60 s) by the others. 403
    /// <c>org_owner_required</c> (only the owner touches an administrator), 404 <c>org_member_not_found</c>, 422
    /// <c>org_member_scope_not_supported</c> and <c>org_member_property_unknown</c>.
    /// </summary>
    [HttpPut("{id:guid}/properties")]
    [ProducesResponseType(typeof(OrgMemberPropertiesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgMemberPropertiesDto>> SetProperties(
        Guid id,
        [FromBody] SetOrgMemberPropertiesRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var view = await propertyAccess.SetAsync(
            caller.OrgId,
            id,
            caller.UserId,
            request.PropertyScope!.Value,
            request.PropertyIds ?? [],
            cancellationToken);
        return Ok(OrgMemberPropertiesDto.From(view));
    }

    /// <summary>The org's people (owner first, then by name) and its seats: used, available, what the plan allows.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(OrgMembersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrgMembersResponse>> List(CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        return Ok(OrgMembersResponse.From(await team.ListAsync(caller.OrgId, cancellationToken)));
    }

    /// <summary>
    /// Gives the member another role; the areas it works in stay. 403 <c>org_owner_required</c> (only the owner makes or
    /// touches an administrator), 404 <c>org_member_not_found</c>, 409 <c>org_last_owner</c>, 422 <c>org_owner_not_assignable</c>.
    /// </summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(OrgMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgMemberDto>> ChangeRole(
        Guid id,
        [FromBody] ChangeOrgMemberRoleRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var member = await team.ChangeRoleAsync(caller.OrgId, id, request.Role, caller.UserId, cancellationToken);
        return Ok(OrgMemberDto.From(member));
    }

    /// <summary>
    /// Switches the member's access off: from its next request it gets 403 <c>member_inactive</c>, and its seat is free.
    /// Its CasaZen account and its Auth0 login are not touched. Idempotent. 403 <c>org_owner_required</c>, 404
    /// <c>org_member_not_found</c>, 409 <c>org_last_owner</c>.
    /// </summary>
    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType(typeof(OrgMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgMemberDto>> Deactivate(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var member = await team.DeactivateAsync(caller.OrgId, id, caller.UserId, cancellationToken);
        return Ok(OrgMemberDto.From(member));
    }

    /// <summary>
    /// Gives the access back; takes a seat again, so it answers 409 <c>org_seat_limit_reached</c> when the plan has none
    /// left. Idempotent. 403 <c>org_owner_required</c>, 404 <c>org_member_not_found</c>.
    /// </summary>
    [HttpPost("{id:guid}/reactivate")]
    [ProducesResponseType(typeof(OrgMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrgMemberDto>> Reactivate(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var member = await team.ReactivateAsync(caller.OrgId, id, caller.UserId, cancellationToken);
        return Ok(OrgMemberDto.From(member));
    }

    /// <summary>
    /// Takes the person out of the org for good: its member row, every membership the org gave and the link of its account
    /// to the org; the seat is free. The person can then onboard an org of its own or accept another invitation. 403
    /// <c>org_owner_required</c>, 404 <c>org_member_not_found</c>, 409 <c>org_last_owner</c>.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        await team.RemoveAsync(caller.OrgId, id, caller.UserId, cancellationToken);
        return NoContent();
    }
}
