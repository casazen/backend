using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IGuestRepository
{
    /// <summary>
    /// Guest by id, subject to the tenant query filter only (authenticated callers see their org,
    /// system/anonymous callers see every org). Prefer <see cref="GetByIdInOrgAsync"/> in org-scoped flows.
    /// </summary>
    Task<Guest?> GetByIdAsync(Guid id);

    /// <summary>Guest by id, only when it belongs to <paramref name="orgId"/> (TN-1).</summary>
    Task<Guest?> GetByIdInOrgAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Most recent guest of <paramref name="orgId"/> with this e-mail (case-insensitive).</summary>
    Task<Guest?> GetByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the org's guests (newest first), excluding deleted ones, optionally filtered by
    /// name, e-mail or phone. Two queries: count and page.
    /// </summary>
    Task<(IReadOnlyList<Guest> Items, int TotalCount)> GetPageAsync(
        Guid orgId,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Guest> AddAsync(Guest guest);
    Task<Guest> UpdateAsync(Guest guest);
    Task DeleteAsync(Guid id);
    Task<bool> ExistsAsync(Guid id);

    /// <summary>True when <paramref name="orgId"/> already has a guest with this e-mail (case-insensitive).</summary>
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
