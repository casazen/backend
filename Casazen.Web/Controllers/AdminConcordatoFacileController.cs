using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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
    IActivityLog activityLog,
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

        // Check if org already has a gift
        var existingGift = await db.ConcordatoFacileGifts
            .FirstOrDefaultAsync(g => g.OrgId == orgId);

        if (existingGift is not null)
        {
            logger.LogWarning(
                "Admin {UserId} attempted to grant gift to org {OrgId} which already has gift {GiftId}",
                userId, orgId, existingGift.Id);
            return this.ApiProblem(
                StatusCodes.Status409Conflict,
                "concordatofacile_gift_already_exists",
                "ConcordatoFacileGiftAlreadyExists");
        }

        // Create the gift
        var gift = new ConcordatoFacileGift
        {
            OrgId = orgId,
            GrantedBySubscriptionId = request.SubscriptionId,
            GrantedAt = DateTime.UtcNow,
            Status = ConcordatoFacileGiftStatus.Available
        };

        db.ConcordatoFacileGifts.Add(gift);

        // Record activity log
        activityLog.Record(new OrgActivity(
            OrgId: orgId,
            Type: OrgActivityType.AdminGrantedConcordatoFacileGift,
            ActorUserId: userId,
            SubjectId: orgId.ToString()
        ));

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Admin {UserId} granted ConcordatoFacile gift {GiftId} to org {OrgId} for subscription {SubscriptionId}. Reason: {Reason}",
            userId, gift.Id, orgId, request.SubscriptionId, request.Reason);

        return Created($"/api/admin/orgs/{orgId}/concordatofacile/gift", new { giftId = gift.Id });
    }
}
