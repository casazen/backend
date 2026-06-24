using Casazen.Core.Services;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Platform-admin endpoints for supplier management (US-022 / #292, AC3).
/// </summary>
[ApiController]
[Route("api/admin/suppliers")]
[Authorize(Policy = "AdminOnly")]
public class AdminSuppliersController(
    ISupplierService supplierService,
    ILogger<AdminSuppliersController> logger) : ControllerBase
{
    /// <summary>Sends an invite email to a prospective supplier for a given comune.</summary>
    [HttpPost("invite")]
    [ProducesResponseType(typeof(AdminInviteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<AdminInviteResponse>> InviteSupplier(
        [FromBody] AdminInviteSupplierRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var invite = await supplierService.CreateInviteAsync(
                request.Email,
                request.ComuneCode,
                request.Categories,
                request.Message,
                cancellationToken);

            logger.LogInformation(
                "Admin supplier invite created: {InviteId} for {Email} in {Comune}",
                invite.InviteId, request.Email, request.ComuneCode);

            return CreatedAtAction(nameof(InviteSupplier), new AdminInviteResponse
            {
                InviteId = invite.InviteId,
                ExpiresAt = invite.ExpiresAt,
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Pending invite", StringComparison.Ordinal))
        {
            return Conflict(new { error = ex.Message, code = "duplicate_invite" });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Supplier invite email delivery failed for {Email}", request.Email);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message, code = "invite_email_failed" });
        }
    }

    /// <summary>
    /// Retroactive fix: repairs orphaned/duplicate supplier profiles and links users to their orgs.
    /// Idempotent — safe to run multiple times. Returns a report of actions taken.
    /// </summary>
    [HttpPost("fix-orphaned")]
    [ProducesResponseType(typeof(FixOrphanedSupplierOrgsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<FixOrphanedSupplierOrgsResponse>> FixOrphaned(CancellationToken cancellationToken)
    {
        var report = await supplierService.FixOrphanedSupplierOrgsAsync(cancellationToken);

        logger.LogInformation(
            "Admin fix-orphaned: scanned={Scanned}, linked={Linked}, merged={Merged}, deleted={Deleted}, orphans={Orphans}",
            report.ProfilesScanned, report.UsersLinked, report.DuplicatesMerged, report.EmptyOrgsDeleted, report.OrphansSkipped);

        return Ok(new FixOrphanedSupplierOrgsResponse
        {
            ProfilesScanned = report.ProfilesScanned,
            UsersLinked = report.UsersLinked,
            DuplicatesMerged = report.DuplicatesMerged,
            EmptyOrgsDeleted = report.EmptyOrgsDeleted,
            OrphansSkipped = report.OrphansSkipped,
            Details = report.Details,
        });
    }
}
