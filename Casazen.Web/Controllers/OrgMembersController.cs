using Casazen.Core.Services;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.Orgs;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// The people of the caller's org (AM-02): list them with the seats, change a role, deactivate, reactivate and remove. For
/// the owner and the administrators (policy <see cref="CasazenPolicies.OrgMembersManage"/>); an administrator is in the
/// hands of the owner only, the owner is never touched (<c>org_last_owner</c>) and nobody becomes owner. <c>{id}</c> is the
/// member row (<c>OrgMemberDto.Id</c>), not the account id. Since AM-03 also the properties a member reaches
/// (<c>{id}/properties</c>). Runbook <c>docs/runbooks/org-team.md</c>.
/// </summary>
[ApiController]
[Route("api/orgs/me/members")]
[Authorize(Policy = CasazenPolicies.OrgMembersManage)]
public class OrgMembersController(
    IOrgTeamService team,
    IOrgPropertyAccessService propertyAccess,
    IOrgContextResolver orgContextResolver) : ControllerBase
{
    public sealed record MarketingGrant(bool Enabled);

    [HttpGet("{id:guid}/marketing")]
    public Task<ActionResult<MarketingGrant>> GetMarketing(Guid id, CancellationToken ct) =>
        MarketingAccessAsync(id, null, ct);

    [HttpPut("{id:guid}/marketing")]
    public Task<ActionResult<MarketingGrant>> SetMarketing(Guid id, MarketingGrant grant, CancellationToken ct) =>
        MarketingAccessAsync(id, grant, ct);

    private async Task<ActionResult<MarketingGrant>> MarketingAccessAsync(Guid id, MarketingGrant? grant, CancellationToken ct)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, ct);
        if (caller.Problem != null) return caller.Problem;
        var db = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        if (!await db.OrgMembers.AnyAsync(m => m.OrgId == caller.OrgId && m.UserId == caller.UserId &&
            m.Role == OrgRole.Owner && m.Status == OrgMemberStatus.Active, ct)) return Forbid();
        var target = await db.OrgMembers.SingleOrDefaultAsync(m => m.Id == id && m.OrgId == caller.OrgId &&
            m.Role == OrgRole.Collaborator && m.Status == OrgMemberStatus.Active, ct);
        if (target == null) return NotFound();
        if (grant != null)
        {
            target.MarketingGranted = grant.Enabled;
            await db.SaveChangesAsync(ct);
            HttpContext.RequestServices.GetRequiredService<IUserAuthorizationCache>().Invalidate(target.UserId);
        }
        return Ok(new MarketingGrant(target.MarketingGranted));
    }

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

    [HttpGet]
    [ProducesResponseType(typeof(OrgMembersResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<OrgMembersResponse>> List(CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        return Ok(OrgMembersResponse.From(await team.ListAsync(caller.OrgId, cancellationToken)));
    }

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

    [HttpPut("{id:guid}/areas")]
    [ProducesResponseType(typeof(OrgMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<OrgMemberDto>> SetAreas(
        Guid id,
        [FromBody] SetOrgMemberAreasRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await this.ResolveOrgTeamCallerAsync(orgContextResolver, cancellationToken);
        if (caller.Problem is not null)
            return caller.Problem;

        var member = await team.SetAreasAsync(caller.OrgId, id, request.Areas ?? [], caller.UserId, cancellationToken);
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
