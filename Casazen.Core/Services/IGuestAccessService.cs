namespace Casazen.Core.Services;

/// <summary>
/// Verifies guest access scoped to an organization's bookings (IDOR guard).
/// </summary>
public interface IGuestAccessService
{
    /// <summary>Returns true when the guest has at least one booking in the given org.</summary>
    Task<bool> IsGuestAccessibleAsync(Guid guestId, Guid orgId, CancellationToken cancellationToken = default);
}
