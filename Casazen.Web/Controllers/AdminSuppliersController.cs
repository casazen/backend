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
    /// <summary>
    /// Creates an invite for a prospective supplier of a given comune and queues its email (delivered by a Hangfire
    /// job, so a provider error no longer fails the request).
    /// </summary>
    [HttpPost("invite")]
    [ProducesResponseType(typeof(AdminInviteResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
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

    /// <summary>
    /// Stored service categories that are not canonical codes (SU-03): values the migration
    /// <c>NormalizeServiceCategories</c> could not map are kept and listed here, so an admin can fix them
    /// (runbook <c>docs/runbooks/service-categories.md</c>).
    /// </summary>
    [HttpGet("unmapped-categories")]
    [ProducesResponseType(typeof(UnmappedServiceCategoriesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<UnmappedServiceCategoriesResponse>> GetUnmappedCategories(CancellationToken cancellationToken)
    {
        var unmapped = await supplierService.GetUnmappedCategoriesAsync(cancellationToken);

        if (unmapped.Count > 0)
            logger.LogWarning("Admin report: {Count} stored service categories are not canonical codes", unmapped.Count);

        return Ok(new UnmappedServiceCategoriesResponse
        {
            Items = unmapped
                .Select(u => new UnmappedServiceCategoryDto { Source = u.Source, Id = u.Id, Value = u.Value })
                .ToList(),
            Total = unmapped.Count,
        });
    }
}
