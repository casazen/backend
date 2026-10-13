namespace Casazen.Core.Services;

/// <summary>
/// Verifies guest access scoped to an organization (IDOR guard, TN-1).
/// </summary>
public interface IGuestAccessService
{
    /// <summary>
    /// Returns true only when the guest belongs to the given org. Having a booking with the guest in
    /// the org is not enough: a guest row is never shared between orgs.
    /// </summary>
    Task<bool> IsGuestAccessibleAsync(Guid guestId, Guid orgId, CancellationToken cancellationToken = default);
}
