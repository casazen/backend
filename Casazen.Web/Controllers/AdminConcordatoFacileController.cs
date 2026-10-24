using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs.ConcordatoFacile;
using Casazen.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Web.Controllers;

/// <summary>Admin endpoints for ConcordatoFacile gift management.</summary>
[ApiController]
[Route("api/admin/orgs/{orgId}/concordatofacile")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminConcordatoFacileController(
    AppDbContext db,
    IConcordatoFacileAdminService concordatoFacileService,
    ILogger<AdminConcordatoFacileController> logger) : ControllerBase
{
    /// <summary>Manually grant a ConcordatoFacile gift to an organization with a biennial subscription.</summary>
    /// <param name="orgId">The organization ID.</param>
    /// <param name="request">The grant request containing subscription ID and reason.</param>
    /// <returns>201 Created if the gift was granted successfully, 409 if the org already has a gift.</returns>
    [HttpPost("grant-gift")]
    public async Task<IActionResult> GrantGift(Guid orgId, [FromBody] AdminGrantGiftRequest request)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var userId = User.GetUserId();
        if (userId is null)
            return Unauthorized();

        // Check if org exists
        if (!await db.Orgs.AnyAsync(o => o.Id == orgId))
            return NotFound();

        try
        {
            var giftId = await concordatoFacileService.GrantGiftAsync(orgId, request.SubscriptionId, userId);

            logger.LogInformation(
                "Admin {UserId} granted ConcordatoFacile gift {GiftId} to org {OrgId} for subscription {SubscriptionId}. Reason: {Reason}",
                userId, giftId, orgId, request.SubscriptionId, request.Reason);

            return Created($"/api/admin/orgs/{orgId}/concordatofacile/gift", new { giftId });
        }
        catch (DomainConflictException ex)
        {
            return this.ApiProblem(StatusCodes.Status409Conflict, ex.Code, ex.MessageKey);
        }
    }
}
