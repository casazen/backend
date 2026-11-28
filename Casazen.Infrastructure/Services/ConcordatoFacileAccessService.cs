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
        if (user.IsInRole("Admin"))
        {
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

        var orgId = tenantContext.OrgId;
        if (!orgId.HasValue)
        {
            return new CfAccessResult(
                HasAccess: false,
                Source: CfAccessSource.None,
                GiftAvailable: false,
                GiftConsumed: false,
                PurchasedAt: null,
                GiftAssignedAt: null);
        }

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
