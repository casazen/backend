namespace Casazen.Core.Services;

public sealed record InviteSupplierRequest(string Email, List<Guid> PropertyIds, string? VatNumber = null, string? FiscalCode = null, string? Phone = null);

public sealed record InviteSupplierResult(Guid InviteId, DateTime ExpiresAt);

public sealed record TrustedSupplierSummary(Guid Id, Guid OrgId, Guid SupplierOrgId, List<Guid> AssignedPropertyIds,
    int Priority, bool VisibleInMarketplace, DateTime CreatedAt, string? SupplierName, bool HostOrigin);

public interface ITrustedSupplierService
{
    /// <summary>
    /// Invites a supplier to become a trusted supplier of the agency (bug #977): a supplier invite of the org
    /// (<see cref="ISupplierService.CreateTrustedSupplierInviteAsync"/>), emailed with the link of the supplier registration.
    /// The supplier accepts it there, signed in with the invited email: a new supplier registers, one already registered
    /// just confirms, and the relation with the org is created then.
    /// </summary>
    /// <param name="orgId">The agency org inviting the supplier.</param>
    /// <param name="request">Invitation details (email, property scope).</param>
    /// <param name="actorUserId">The user creating the invitation.</param>
    /// <returns>The invite and its expiry.</returns>
    /// <exception cref="Casazen.Core.Exceptions.DomainRuleException">
    /// Codes <c>trusted_supplier_invite_email_invalid</c>, <c>trusted_supplier_property_invalid</c> (a property that is not
    /// the org's), <c>trusted_supplier_invite_comune_missing</c>.
    /// </exception>
    /// <exception cref="Casazen.Core.Exceptions.DomainConflictException">
    /// Codes <c>trusted_supplier_already_added</c>, <c>duplicate_invite</c>.
    /// </exception>
    Task<InviteSupplierResult> InviteSupplierAsync(Guid orgId, InviteSupplierRequest request, string actorUserId);

    Task<List<TrustedSupplierSummary>> GetTrustedSuppliersAsync(Guid orgId);

    Task UpdateMarketplaceVisibilityAsync(Guid orgId, Guid trustedSupplierId, bool visible, string actorUserId);

    Task RemoveTrustedSupplierAsync(Guid orgId, Guid trustedSupplierId, string actorUserId);
}
