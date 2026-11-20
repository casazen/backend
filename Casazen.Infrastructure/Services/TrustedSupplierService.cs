using System.ComponentModel.DataAnnotations;
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
    ISupplierService supplierService,
    IActivityLog activityLog,
    ILogger<TrustedSupplierService> logger) : ITrustedSupplierService
{
    /// <summary>The length of <see cref="SupplierInviteRecord.Email"/>.</summary>
    private const int EmailMaxLength = 255;

    public async Task<InviteSupplierResult> InviteSupplierAsync(Guid orgId, InviteSupplierRequest request, string actorUserId)
    {
        var email = request.Email?.Trim() ?? string.Empty;
        if (email.Length == 0 || email.Length > EmailMaxLength || !new EmailAddressAttribute().IsValid(email))
            throw new DomainRuleException("trusted_supplier_invite_email_invalid", "TrustedSupplierInviteEmailInvalid");

        // Only the org's own properties can be assigned (the tenant filter keeps the others out of the count too).
        var propertyIds = (request.PropertyIds ?? []).Distinct().ToList();
        if (propertyIds.Count > 0
            && await db.Properties.CountAsync(p => p.OrgId == orgId && propertyIds.Contains(p.Id)) != propertyIds.Count)
            throw new DomainRuleException("trusted_supplier_property_invalid", "TrustedSupplierPropertyInvalid");

        // A supplier with this email that already works for the org has nothing to accept.
        var normalized = SupplierProfileEmailIndex.Normalize(email);
        var alreadyTrusted = await db.TrustedSuppliers
            .Where(ts => ts.OrgId == orgId)
            .Join(db.SupplierProfiles, ts => ts.SupplierOrgId, sp => sp.OrgId, (ts, sp) => sp.Email)
            .AnyAsync(profileEmail => profileEmail.Trim().ToLower() == normalized);
        if (alreadyTrusted)
            throw new DomainConflictException("trusted_supplier_already_added", "TrustedSupplierAlreadyAdded");

        // The invite, its email and its acceptance are the ones of the supplier registration (#977).
        var invite = await supplierService.CreateTrustedSupplierInviteAsync(orgId, email, propertyIds);

        logger.LogInformation(
            "Trusted supplier invite {InviteId} of org {OrgId} created by {Actor}", invite.InviteId, orgId, actorUserId);
        return new InviteSupplierResult(invite.InviteId, invite.ExpiresAt);
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
            throw new NotFoundException($"Trusted supplier {trustedSupplierId} not found for org {orgId}")
            {
                Code = "trusted_supplier_not_found",
                MessageKey = "TrustedSupplierNotFound"
            };
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
            throw new NotFoundException($"Trusted supplier {trustedSupplierId} not found for org {orgId}")
            {
                Code = "trusted_supplier_not_found",
                MessageKey = "TrustedSupplierNotFound"
            };
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
