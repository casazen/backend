using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="ITrustedSupplierService" />
public sealed class TrustedSupplierService(
    AppDbContext db,
    IActivityLog activityLog,
    ILogger<TrustedSupplierService> logger,
    TimeProvider? timeProvider = null) : ITrustedSupplierService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private static readonly TimeSpan InvitationExpiry = TimeSpan.FromDays(7);

    public Task<InviteSupplierResult> InviteSupplierAsync(Guid orgId, InviteSupplierRequest request, string actorUserId)
    {
        // For wave 2, the invitation mechanism is simplified:
        // Generate a token and return it. The actual invitation delivery
        // (email, notification) is handled by the caller.
        var token = Guid.NewGuid().ToString("N");
        var expiresAt = _clock.GetUtcNow().Add(InvitationExpiry).UtcDateTime;

        logger.LogInformation(
            "Trusted supplier invitation created: OrgId={OrgId} Email={Email} Actor={Actor} Token={Token}",
            orgId, request.Email, actorUserId, token);

        return Task.FromResult(new InviteSupplierResult(token, expiresAt));
    }

    public async Task<TrustedSupplier> AcceptInviteAsync(AcceptSupplierInviteRequest request, string actorUserId)
    {
        // For wave 2, accept the invitation and create the relationship.
        // In a real implementation, we would validate the token against stored invitations.
        // For now, we create the relationship directly.

        // Note: In production, this would validate the token, check expiry,
        // verify the supplier org exists, and ensure no duplicate relationship exists.

        throw new NotImplementedException("AcceptInviteAsync requires invitation storage mechanism");
    }

    public async Task<List<TrustedSupplier>> GetTrustedSuppliersAsync(Guid orgId)
    {
        return await db.TrustedSuppliers
            .Where(ts => ts.OrgId == orgId)
            .OrderBy(ts => ts.Priority)
            .ThenBy(ts => ts.CreatedAt)
            .ToListAsync();
    }

    public async Task UpdateMarketplaceVisibilityAsync(Guid orgId, Guid trustedSupplierId, bool visible, string actorUserId)
    {
        var trustedSupplier = await db.TrustedSuppliers
            .FirstOrDefaultAsync(ts => ts.Id == trustedSupplierId && ts.OrgId == orgId);

        if (trustedSupplier == null)
        {
            throw new DomainNotFoundException(
                "trusted_supplier_not_found",
                $"Trusted supplier {trustedSupplierId} not found for org {orgId}");
        }

        trustedSupplier.VisibleInMarketplace = visible;
        await db.SaveChangesAsync();

        logger.LogInformation(
            "Trusted supplier marketplace visibility updated: Id={Id} Visible={Visible} Actor={Actor}",
            trustedSupplierId, visible, actorUserId);
    }

    public async Task RemoveTrustedSupplierAsync(Guid orgId, Guid trustedSupplierId, string actorUserId)
    {
        var trustedSupplier = await db.TrustedSuppliers
            .FirstOrDefaultAsync(ts => ts.Id == trustedSupplierId && ts.OrgId == orgId);

        if (trustedSupplier == null)
        {
            throw new DomainNotFoundException(
                "trusted_supplier_not_found",
                $"Trusted supplier {trustedSupplierId} not found for org {orgId}");
        }

        db.TrustedSuppliers.Remove(trustedSupplier);

        // Record activity event
        activityLog.Record(OrgActivity.Of(
            orgId,
            OrgActivityType.TrustedSupplierRemoved,
            actorUserId,
            trustedSupplier.SupplierOrgId.ToString()));

        await db.SaveChangesAsync();

        logger.LogInformation(
            "Trusted supplier removed: Id={Id} SupplierOrgId={SupplierOrgId} Actor={Actor}",
            trustedSupplierId, trustedSupplier.SupplierOrgId, actorUserId);
    }
}
