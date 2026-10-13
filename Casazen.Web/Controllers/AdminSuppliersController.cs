using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Core.Validation;
using Casazen.Web.Authorization;
using Casazen.Web.DTOs;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Casazen.Web.Controllers;

/// <summary>
/// Platform-admin endpoints for supplier management (US-022 / #292, AC3).
/// </summary>
[ApiController]
[Route("api/admin/suppliers")]
[Authorize(Policy = CasazenPolicies.AdminOnly)]
public class AdminSuppliersController(
    ISupplierService supplierService,
    ISupplierAdminService supplierAdminService,
    ILogger<AdminSuppliersController> logger) : ControllerBase
{
    /// <summary>
    /// The suppliers, newest first, paginated in SQL (SU-12, A4-29). <c>status</c> is <c>Pending</c>, <c>Active</c> or
    /// <c>Suspended</c> (anything else leaves the filter off); <c>search</c> matches the legal name or the email.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<AdminSupplierDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<AdminSupplierDto>>> List(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        // Out-of-range values would become a negative OFFSET/LIMIT in SQL, i.e. a 500: clamp them.
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Enum.TryParse alone also accepts a numeric string with no declared member: it would filter by nothing.
        SupplierStatus? statusFilter = EnumNames.TryParseDefined<SupplierStatus>(status, out var parsed) ? parsed : null;

        var (items, total) = await supplierAdminService.ListAsync(
            new AdminSupplierListQuery(search, statusFilter, page, pageSize), cancellationToken);

        return Ok(new PagedResultDto<AdminSupplierDto>
        {
            Items = items.Select(AdminSupplierDto.From),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// Suspends a supplier (SU-12, A4-29): it receives no new request and can no longer take, complete or reject one.
    /// The reason is required (400 otherwise) and recorded in the audit trail with the admin's id. 404
    /// <c>supplier_not_found</c>, 409 <c>supplier_already_suspended</c>.
    /// </summary>
    [HttpPost("{orgId:guid}/suspend")]
    [ProducesResponseType(typeof(AdminSupplierDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminSupplierDto>> Suspend(
        Guid orgId,
        [FromBody] SuspendSupplierRequest request,
        CancellationToken cancellationToken)
    {
        var item = await supplierAdminService.SuspendAsync(orgId, ActorUserId(), request.Reason, cancellationToken);
        return Ok(AdminSupplierDto.From(item));
    }

    /// <summary>
    /// Reactivates a suspended supplier: back to <c>Active</c> when it had accepted the terms, otherwise to
    /// <c>Pending</c> (the activation wizard is never skipped). 404 <c>supplier_not_found</c>, 409
    /// <c>supplier_not_suspended</c>.
    /// </summary>
    [HttpPost("{orgId:guid}/reactivate")]
    [ProducesResponseType(typeof(AdminSupplierDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminSupplierDto>> Reactivate(Guid orgId, CancellationToken cancellationToken)
    {
        var item = await supplierAdminService.ReactivateAsync(orgId, ActorUserId(), cancellationToken);
        return Ok(AdminSupplierDto.From(item));
    }

    /// <summary>The audit trail of a supplier (suspensions and reactivations), newest first, at most 50 lines.</summary>
    [HttpGet("{orgId:guid}/audit")]
    [ProducesResponseType(typeof(IEnumerable<SupplierAdminAuditDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IEnumerable<SupplierAdminAuditDto>>> GetAudit(
        Guid orgId,
        CancellationToken cancellationToken)
    {
        var entries = await supplierAdminService.GetAuditAsync(orgId, limit: 50, cancellationToken);
        return Ok(entries.Select(SupplierAdminAuditDto.From));
    }

    /// <summary>
    /// The invites, newest first, paginated in SQL. <c>state</c> is <c>Pending</c>, <c>Used</c>, <c>Expired</c> or
    /// <c>Revoked</c> (anything else leaves the filter off); <c>search</c> matches the invited email.
    /// </summary>
    [HttpGet("invites")]
    [ProducesResponseType(typeof(PagedResultDto<AdminInviteDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<AdminInviteDto>>> ListInvites(
        [FromQuery] string? search,
        [FromQuery] string? state,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        SupplierInviteState? stateFilter = EnumNames.TryParseDefined<SupplierInviteState>(state, out var parsed) ? parsed : null;

        var (items, total) = await supplierAdminService.ListInvitesAsync(
            new AdminInviteListQuery(search, stateFilter, page, pageSize), cancellationToken);

        return Ok(new PagedResultDto<AdminInviteDto>
        {
            Items = items.Select(AdminInviteDto.From),
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    /// <summary>
    /// Sends a pending or expired invite again: a new link and a new 7-day expiry, the old link stops working. 404
    /// <c>supplier_invite_not_found</c>, 409 <c>supplier_invite_not_resendable</c> (used or revoked),
    /// <c>duplicate_invite</c> or <c>supplier_email_taken</c>.
    /// </summary>
    [HttpPost("invites/{inviteId:guid}/resend")]
    [ProducesResponseType(typeof(AdminInviteResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AdminInviteResponse>> ResendInvite(Guid inviteId, CancellationToken cancellationToken)
    {
        var invite = await supplierAdminService.ResendInviteAsync(inviteId, ActorUserId(), cancellationToken);
        return Ok(new AdminInviteResponse { InviteId = invite.InviteId, ExpiresAt = invite.ExpiresAt });
    }

    /// <summary>
    /// Revokes a pending invite: its link no longer works. 404 <c>supplier_invite_not_found</c>, 409
    /// <c>supplier_invite_not_pending</c> (used, expired or already revoked).
    /// </summary>
    [HttpDelete("invites/{inviteId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RevokeInvite(Guid inviteId, CancellationToken cancellationToken)
    {
        await supplierAdminService.RevokeInviteAsync(inviteId, ActorUserId(), cancellationToken);
        return NoContent();
    }

    /// <summary>Auth0 subject of the admin: AdminOnly guarantees an authenticated user, whose token always has one.</summary>
    private string ActorUserId() =>
        User.GetUserId() ?? throw new UnauthorizedAccessException("Admin without subject claim");

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

    /// <summary>
    /// Repairs supplier profiles (SU-14, A4-22): merges the profiles that share an email into one keeper (service
    /// requests, availability, the price catalog of SP-02, the agenda of SP-03, categories, comuni and accounts move,
    /// nothing answers 500),
    /// unlinks accounts from deleted
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
                    ServiceListingsMoved = m.ServiceListingsMoved,
                    AgendaRowsMoved = m.AgendaRowsMoved,
                    ShowcaseRowsMoved = m.ShowcaseRowsMoved,
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
