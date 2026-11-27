using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Manages ConcordatoFacile gift granting and consumption (ADR-006).
/// Gifts are granted via webhook when a biennial subscription is created,
/// and consumed atomically for a lease with PostgreSQL advisory locks.
/// </summary>
public sealed class ConcordatoFacileGiftService(
    AppDbContext dbContext,
    ILogger<ConcordatoFacileGiftService> logger) : IConcordatoFacileGiftService
{
    public async Task GrantGiftAsync(Guid orgId, string subscriptionId, CancellationToken cancellationToken = default)
    {
        // ADR-006-R3: Idempotent - check if gift already exists for this org
        var existingGift = await dbContext.ConcordatoFacileGifts
            .Where(g => g.OrgId == orgId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingGift is not null)
        {
            logger.LogInformation(
                "Gift already assigned to org {OrgId} (existing gift {GiftId}, subscription {ExistingSubscriptionId}); subscription {SubscriptionId} skipped",
                orgId,
                existingGift.Id,
                existingGift.GrantedBySubscriptionId,
                subscriptionId);
            return;
        }

        var gift = new ConcordatoFacileGift
        {
            OrgId = orgId,
            GrantedBySubscriptionId = subscriptionId,
            GrantedAt = DateTime.UtcNow,
            Status = ConcordatoFacileGiftStatus.Available
        };

        dbContext.ConcordatoFacileGifts.Add(gift);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ConcordatoFacile gift {GiftId} granted to org {OrgId} by subscription {SubscriptionId}",
            gift.Id,
            orgId,
            subscriptionId);
    }

    public async Task<CfGiftConsumeResult> ConsumeGiftAsync(
        Guid orgId,
        Guid leaseId,
        CancellationToken cancellationToken = default)
    {
        await dbContext.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

        // ADR-006-R4: Acquire PostgreSQL advisory lock for atomicity
        // The lock key is the org ID to serialize gift consumption for the same org
        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            dbContext,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OrgBillingCheckout, $"concordato-gift-{orgId}"));

        var consumedForThisLease = await dbContext.ConcordatoFacileGifts
            .Where(g => g.OrgId == orgId && g.ConsumedForLeaseId == leaseId)
            .FirstOrDefaultAsync(cancellationToken);

        if (consumedForThisLease is not null)
        {
            logger.LogInformation(
                "Gift {GiftId} already consumed for lease {LeaseId} (re-entry allowed)",
                consumedForThisLease.Id,
                leaseId);
            return CfGiftConsumeResult.AlreadyConsumedForThisLease;
        }

        var availableGift = await dbContext.ConcordatoFacileGifts
            .Where(g => g.OrgId == orgId
                && g.Status == ConcordatoFacileGiftStatus.Available
                && g.ConsumedForLeaseId == null)
            .FirstOrDefaultAsync(cancellationToken);

        if (availableGift is null)
        {
            logger.LogInformation(
                "No available gift for org {OrgId} (consumption for lease {LeaseId} failed)",
                orgId,
                leaseId);
            return CfGiftConsumeResult.AlreadyConsumedForOtherLease;
        }

        var now = DateTime.UtcNow;
        availableGift.ConsumedForLeaseId = leaseId;
        availableGift.ConsumedAt = now;
        availableGift.Status = ConcordatoFacileGiftStatus.Consumed;

        var leaseEvent = new LeaseEvent
        {
            LeaseContractId = leaseId,
            EventType = LeaseEventType.ConcordatoFacileGiftConsumed,
            OccurredAt = now,
            Payload = null
        };
        dbContext.LeaseEvents.Add(leaseEvent);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "ConcordatoFacile gift {GiftId} consumed for lease {LeaseId} of org {OrgId}",
            availableGift.Id,
            leaseId,
            orgId);

        return CfGiftConsumeResult.Success;
    }
}
