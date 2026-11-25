using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Request to invite a supplier to become a trusted supplier of an agency.
/// </summary>
/// <param name="Email">Supplier email to invite.</param>
/// <param name="PropertyIds">Properties the supplier may work on (empty = all).</param>
public sealed record InviteSupplierRequest(string Email, List<Guid> PropertyIds);

/// <summary>
/// Result of a supplier invitation. The link token only travels in the email to the supplier (A4-04).
/// </summary>
/// <param name="InviteId">The invite.</param>
/// <param name="ExpiresAt">When the invitation expires.</param>
public sealed record InviteSupplierResult(Guid InviteId, DateTime ExpiresAt);

public sealed record TrustedSupplierSummary(Guid Id, Guid OrgId, Guid SupplierOrgId, List<Guid> AssignedPropertyIds,
    int Priority, bool VisibleInMarketplace, DateTime CreatedAt, string? SupplierName, bool HostOrigin);

/// <summary>
/// Manages trusted supplier relationships for agency tier orgs (NF-21, ADR-020).
/// Trusted suppliers work with PaymentMode.Manual, 0% commission, and their
/// approved amounts flow to owner statements.
/// </summary>
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

    /// <summary>
    /// Gets all trusted suppliers for an agency org.
    /// </summary>
    /// <param name="orgId">The agency org.</param>
    /// <returns>List of trusted suppliers.</returns>
    Task<List<TrustedSupplierSummary>> GetTrustedSuppliersAsync(Guid orgId);

    /// <summary>
    /// Toggles marketplace visibility for a trusted supplier.
    /// </summary>
    /// <param name="orgId">The agency org.</param>
    /// <param name="trustedSupplierId">The trusted supplier ID.</param>
    /// <param name="visible">New visibility state.</param>
    /// <param name="actorUserId">The user making the change.</param>
    Task UpdateMarketplaceVisibilityAsync(Guid orgId, Guid trustedSupplierId, bool visible, string actorUserId);

    /// <summary>
    /// Removes a trusted supplier relationship.
    /// Records TrustedSupplierRemoved activity event.
    /// </summary>
    /// <param name="orgId">The agency org.</param>
    /// <param name="trustedSupplierId">The trusted supplier ID.</param>
    /// <param name="actorUserId">The user removing the supplier.</param>
    Task RemoveTrustedSupplierAsync(Guid orgId, Guid trustedSupplierId, string actorUserId);
}
