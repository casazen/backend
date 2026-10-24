using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IConcordatoFacileAdminService" />
public sealed class ConcordatoFacileAdminService(
    AppDbContext db,
    IActivityLog activityLog,
    ILogger<ConcordatoFacileAdminService> logger) : IConcordatoFacileAdminService
{
    public async Task<Guid> GrantGiftAsync(Guid orgId, string subscriptionId, string actorUserId, CancellationToken ct = default)
    {
        // Check if org already has a gift
        var existingGift = await db.ConcordatoFacileGifts
            .FirstOrDefaultAsync(g => g.OrgId == orgId, ct);

        if (existingGift is not null)
        {
            logger.LogWarning(
                "Admin {UserId} attempted to grant gift to org {OrgId} which already has gift {GiftId}",
                actorUserId, orgId, existingGift.Id);
            throw new DomainConflictException(
                "concordatofacile_gift_already_exists",
                "ConcordatoFacileGiftAlreadyExists");
        }

        // Create the gift
        var gift = new ConcordatoFacileGift
        {
            OrgId = orgId,
            GrantedBySubscriptionId = subscriptionId,
            GrantedAt = DateTime.UtcNow,
            Status = ConcordatoFacileGiftStatus.Available
        };

        db.ConcordatoFacileGifts.Add(gift);

        // Record activity log inside the same transaction
        activityLog.Record(new OrgActivity(
            OrgId: orgId,
            Type: OrgActivityType.AdminGrantedConcordatoFacileGift,
            ActorUserId: actorUserId,
            SubjectId: orgId.ToString()
        ));

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Admin {UserId} granted ConcordatoFacile gift {GiftId} to org {OrgId} for subscription {SubscriptionId}",
            actorUserId, gift.Id, orgId, subscriptionId);

        return gift.Id;
    }
}
