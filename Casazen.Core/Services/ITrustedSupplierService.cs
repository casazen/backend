using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Request to invite a supplier to become a trusted supplier of an agency.
/// </summary>
/// <param name="Email">Supplier email to invite.</param>
/// <param name="PropertyIds">Properties the supplier may work on (empty = all).</param>
public sealed record InviteSupplierRequest(string Email, List<Guid> PropertyIds);

/// <summary>
/// Result of a supplier invitation.
/// </summary>
/// <param name="InvitationToken">Token to be sent to the supplier for acceptance.</param>
/// <param name="ExpiresAt">When the invitation expires.</param>
public sealed record InviteSupplierResult(string InvitationToken, DateTime ExpiresAt);

/// <summary>
/// Request to accept a trusted supplier invitation.
/// </summary>
/// <param name="Token">The invitation token.</param>
/// <param name="SupplierOrgId">The supplier's org accepting the invitation.</param>
public sealed record AcceptSupplierInviteRequest(string Token, Guid SupplierOrgId);

/// <summary>
/// Manages trusted supplier relationships for agency tier orgs (NF-21, ADR-020).
/// Trusted suppliers work with PaymentMode.Manual, 0% commission, and their
/// approved amounts flow to owner statements.
/// </summary>
public interface ITrustedSupplierService
{
    /// <summary>
    /// Invites a supplier to become a trusted supplier of the agency.
    /// Generates an invitation token that the supplier can use to accept.
    /// </summary>
    /// <param name="orgId">The agency org inviting the supplier.</param>
    /// <param name="request">Invitation details (email, property scope).</param>
    /// <param name="actorUserId">The user creating the invitation.</param>
    /// <returns>Invitation token and expiration.</returns>
    Task<InviteSupplierResult> InviteSupplierAsync(Guid orgId, InviteSupplierRequest request, string actorUserId);

    /// <summary>
    /// Accepts a trusted supplier invitation and creates the relationship.
    /// Records TrustedSupplierAdded activity event.
    /// </summary>
    /// <param name="request">Token and supplier org.</param>
    /// <param name="actorUserId">The user accepting the invitation.</param>
    /// <returns>The created TrustedSupplier relationship.</returns>
    Task<TrustedSupplier> AcceptInviteAsync(AcceptSupplierInviteRequest request, string actorUserId);

    /// <summary>
    /// Gets all trusted suppliers for an agency org.
    /// </summary>
    /// <param name="orgId">The agency org.</param>
    /// <returns>List of trusted suppliers.</returns>
    Task<List<TrustedSupplier>> GetTrustedSuppliersAsync(Guid orgId);

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
