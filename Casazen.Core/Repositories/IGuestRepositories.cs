using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IGuestRepository
{
    Task<Guest?> GetByIdAsync(Guid id);

    Task<Guest?> GetByIdInScopeAsync(HostScope scope, Guid id, CancellationToken cancellationToken = default);

    Task<Guest?> GetByEmailAsync(HostScope scope, string email, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Guest> Items, int TotalCount)> GetPageAsync(
        HostScope scope,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Guest> AddAsync(Guest guest);
    Task<Guest> UpdateAsync(Guest guest);
    Task DeleteAsync(Guid id);
    Task<bool> ExistsAsync(Guid id);

    Task<bool> ExistsByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bookings and Alloggiati reports that reference the guest, across every org (the FK does not care
    /// about tenants), and whether one of the bookings is still in an open lifecycle state.
    /// </summary>
    Task<GuestUsage> GetUsageAsync(Guid guestId, DateTime today, CancellationToken cancellationToken = default);
}

/// <summary>What still references a guest (TN-1, guest deletion).</summary>
/// <param name="HasReferences">At least one booking or Alloggiati report points at the guest.</param>
/// <param name="HasOpenBookings">A pending, confirmed or checked-in booking still exists.</param>
public sealed record GuestUsage(bool HasReferences, bool HasOpenBookings);
