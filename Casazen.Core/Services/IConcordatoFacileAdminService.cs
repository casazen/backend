namespace Casazen.Core.Services;

/// <summary>Admin operations for ConcordatoFacile gift management.</summary>
public interface IConcordatoFacileAdminService
{
    /// <summary>Manually grant a ConcordatoFacile gift to an organization with a biennial subscription.</summary>
    /// <param name="orgId">The organization ID.</param>
    /// <param name="subscriptionId">The Stripe subscription ID that grants the gift.</param>
    /// <param name="actorUserId">The admin user granting the gift.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ID of the created gift.</returns>
    /// <exception cref="DomainConflictException">If the organization already has a gift.</exception>
    Task<Guid> GrantGiftAsync(Guid orgId, string subscriptionId, string actorUserId, CancellationToken ct = default);
}
