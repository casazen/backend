using Casazen.Core.Services;
using Casazen.Core.Utilities;
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
    /// job, so a provider error no longer fails the request). 409 <c>duplicate_invite</c> when a pending invite exists
    /// for the email, 409 <c>supplier_email_taken</c> when a supplier profile already has it (SU-14).
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
                "Admin supplier invite created: {InviteId} for {MaskedEmail} in {Comune}",
                invite.InviteId, LogRedaction.MaskEmail(request.Email), request.ComuneCode);

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
    /// Repairs supplier profiles (SU-14, A4-22): merges the profiles that share an email into one keeper (service
    /// requests, availability, categories, comuni and accounts move, nothing answers 500), unlinks accounts from deleted
    /// supplier orgs and reports the profiles no account holds. Nothing is ever linked by email (A4-23).
    /// <paramref name="dryRun"/> defaults to true: the report shows what would change and nothing is saved; send
    /// <c>dryRun=false</c> to apply. Idempotent. Runbook <c>docs/runbooks/suppliers.md</c> section 9.
    /// </summary>
    [HttpPost("fix-orphaned")]
    [ProducesResponseType(typeof(FixOrphanedSupplierOrgsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<FixOrphanedSupplierOrgsResponse>> FixOrphaned(
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        var report = await supplierService.FixOrphanedSupplierOrgsAsync(dryRun, cancellationToken);

        logger.LogInformation(
            "Admin fix-orphaned (dryRun={DryRun}): scanned={Scanned}, duplicateGroups={Groups}, merged={Merged}, " +
            "manualInterventions={Manual}",
            report.DryRun, report.ProfilesScanned, report.DuplicateGroups, report.Merges.Count,
            report.ManualInterventions.Count);

        return Ok(new FixOrphanedSupplierOrgsResponse
        {
            DryRun = report.DryRun,
            ProfilesScanned = report.ProfilesScanned,
            DuplicateGroups = report.DuplicateGroups,
            DuplicatesMerged = report.Merges.Count,
            ServiceRequestsMoved = report.Merges.Sum(m => m.ServiceRequestsMoved),
            Merges = report.Merges
                .Select(m => new SupplierDuplicateMergeDto
                {
                    KeeperOrgId = m.KeeperOrgId,
                    DuplicateOrgId = m.DuplicateOrgId,
                    ServiceRequestsMoved = m.ServiceRequestsMoved,
                    AvailabilityDaysMoved = m.AvailabilityDaysMoved,
                    AvailabilityDaysDropped = m.AvailabilityDaysDropped,
                    CategoriesAdded = m.CategoriesAdded,
                    ComuniAdded = m.ComuniAdded,
                    SupplierLinksMoved = m.SupplierLinksMoved,
                    OrgMembersMoved = m.OrgMembersMoved,
                    DevicesMoved = m.DevicesMoved,
                    DuplicateOrgDeleted = m.DuplicateOrgDeleted,
                })
                .ToList(),
            DanglingLinksCleared = report.DanglingLinksCleared,
            SupplierLinksBackfilled = report.SupplierLinksBackfilled,
            OrphanProfiles = report.OrphanProfiles,
            ManualInterventions = report.ManualInterventions
                .Select(m => new SupplierManualInterventionDto { Code = m.Code, OrgIds = m.OrgIds, UserIds = m.UserIds })
                .ToList(),
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
