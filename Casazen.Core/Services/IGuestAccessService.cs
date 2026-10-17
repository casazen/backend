using Casazen.Core.Authorization;

namespace Casazen.Core.Services;

/// <summary>
/// Verifies guest access scoped to an organization and to the properties the caller reaches (IDOR guard, TN-1; SR-04b).
/// </summary>
public interface IGuestAccessService
{
    /// <summary>
    /// Returns true only when the guest belongs to the org of <paramref name="scope"/> and the scope reaches it. Having a booking
    /// with the guest in the org is not enough: a guest row is never shared between orgs. A scope limited to some properties
    /// reaches the guests that have a stay at one of them; the whole org also reaches the guests that have none.
    /// </summary>
    Task<bool> IsGuestAccessibleAsync(Guid guestId, HostScope scope, CancellationToken cancellationToken = default);
}
