using Casazen.Core.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Owner invitation endpoints for agency tier (ADR-011-R4).
/// Rate limited to 100 invitations per day per org.
/// </summary>
[ApiController]
[Route("api/owners/{propertyOwnerId:guid}/invite")]
[Authorize(Policy = CasazenPolicies.AgencyOwnerManage)]
public class OwnerInvitationsController(
    IOwnerInvitationService ownerInvitationService,
    IOrgContextResolver orgContextResolver,
    ILogger<OwnerInvitationsController> logger) : ControllerBase
{
    /// <summary>
    /// Invites a property owner to the owner portal (ADR-011-R4).
    /// Generates secure token, sends email. Rate limited to 100/day/org.
    /// Requires <c>agency.owner.manage</c> policy.
    /// </summary>
    /// <param name="propertyOwnerId">The PropertyOwner ID to invite.</param>
    /// <returns>201 Created with the invitation ID.</returns>
    /// <response code="201">Invitation created and email sent.</response>
    /// <response code="404">Property owner not found or not in caller's org.</response>
    /// <response code="429">Daily invitation limit reached (100 per org).</response>
    [HttpPost]
    [ProducesResponseType(typeof(InvitationCreatedDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<InvitationCreatedDto>> CreateInvitation(Guid propertyOwnerId)
    {
        var orgId = await orgContextResolver.GetOrProvisionOrgIdAsync(HttpContext.RequestAborted);
        if (orgId is null)
            return Forbid();

        var userId = User.GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        logger.LogInformation(
            "Creating owner invitation for PropertyOwner {PropertyOwnerId} in org {OrgId}",
            propertyOwnerId, orgId.Value);

        var invitationId = await ownerInvitationService.CreateInvitationAsync(
            orgId.Value,
            propertyOwnerId,
            userId,
            HttpContext.RequestAborted);

        return CreatedAtAction(
            nameof(CreateInvitation),
            new { propertyOwnerId },
            new InvitationCreatedDto(invitationId));
    }
}

/// <summary>Response DTO for invitation creation.</summary>
public record InvitationCreatedDto(Guid InvitationId);
