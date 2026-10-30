using System.Security.Claims;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Checks ConcordatoFacile access for a lease (ADR-005-R8).
/// Access is granted if the user is a platform admin, or the lease has a completed purchase or consumed gift.
/// </summary>
public sealed class ConcordatoFacileAccessService(
    AppDbContext dbContext,
    ITenantContext tenantContext,
    ILogger<ConcordatoFacileAccessService> logger) : IConcordatoFacileAccessService
{
    public async Task<CfAccessResult> CheckAccessAsync(
        Guid leaseId,
        ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        // Check 1: Platform admin (ADR-005-R8, AC-NF04-1)
        if (user.IsInRole("Admin"))
        {
            // Audit admin access (AC-NF04-3)
            var userId = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            await dbContext.LeaseEvents.AddAsync(new LeaseEvent
            {
                LeaseContractId = leaseId,
                EventType = LeaseEventType.ConcordatoFacileAdminAccess,
                Payload = JsonSerializer.Serialize(new { userId }),
            }, cancellationToken);

            logger.LogInformation(
                "Admin access to ConcordatoFacile for lease {LeaseId} by user {UserId}",
                leaseId,
                userId);

            return new CfAccessResult(
                HasAccess: true,
                Source: CfAccessSource.Admin,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: null,
                GiftAssignedAt: null);
        }

        // Get org context for tenant isolation (used in all subsequent checks)
        var orgId = tenantContext.OrgId;
        if (!orgId.HasValue)
        {
            // No tenant context means no org-specific access (admin already returned)
            return new CfAccessResult(
                HasAccess: false,
                Source: CfAccessSource.None,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: null,
                GiftAssignedAt: null);
        }

        // Check 2: Completed purchase for this lease (ADR-005-R8)
        // Explicit OrgId filter ensures tenant isolation even when global filter is disabled
        var purchase = await dbContext.ConcordatoFacilePurchases
            .Where(p => p.OrgId == orgId.Value
                && p.LeaseContractId == leaseId
                && p.Status == ConcordatoFacilePurchaseStatus.Completed)
            .FirstOrDefaultAsync(cancellationToken);

        if (purchase is not null)
        {
            return new CfAccessResult(
                HasAccess: true,
                Source: CfAccessSource.Payment,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: purchase.PaidAt,
                GiftAssignedAt: null);
        }

        // Check 3: Gift consumed for this lease (ADR-005-R8)
        // Explicit OrgId filter ensures tenant isolation even when global filter is disabled
        var consumedGift = await dbContext.ConcordatoFacileGifts
            .Where(g => g.OrgId == orgId.Value
                && g.ConsumedForLeaseId == leaseId)
            .FirstOrDefaultAsync(cancellationToken);

        if (consumedGift is not null)
        {
            return new CfAccessResult(
                HasAccess: true,
                Source: CfAccessSource.Gift,
                GiftAvailable: false,
                GiftConsumed: true,
                PurchasedAt: null,
                GiftAssignedAt: consumedGift.GrantedAt);
        }

        // No access granted - check if user has available gifts (ADR-005-R8, ADR-006 recommendation)
        var hasAvailableGift = await dbContext.ConcordatoFacileGifts
            .Where(g => g.OrgId == orgId.Value && g.ConsumedForLeaseId == null)
            .AnyAsync(cancellationToken);

        return new CfAccessResult(
            HasAccess: false,
            Source: CfAccessSource.None,
            GiftAvailable: hasAvailableGift,
            GiftConsumed: false,
            PurchasedAt: null,
            GiftAssignedAt: null);
    }
}
