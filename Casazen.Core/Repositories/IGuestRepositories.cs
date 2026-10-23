using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Repositories;

public interface IGuestRepository
{
    /// <summary>
    /// Guest by id, subject to the tenant query filter only (authenticated callers see their org,
    /// system/anonymous callers see every org). Prefer <see cref="GetByIdInScopeAsync"/> in org-scoped flows.
    /// </summary>
    Task<Guest?> GetByIdAsync(Guid id);

    /// <summary>
    /// Guest by id, only when it belongs to the org of <paramref name="scope"/> (TN-1) and the scope reaches it (SR-04b): a
    /// scope limited to some properties reaches the guests that have a stay at one of them (<c>InScope</c>), the whole org also
    /// the guests with no stay. A guest outside the scope is reported as missing, as one of another org is.
    /// </summary>
    Task<Guest?> GetByIdInScopeAsync(HostScope scope, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Most recent guest of the org of <paramref name="scope"/> that the scope reaches, with this e-mail (case-insensitive).</summary>
    Task<Guest?> GetByEmailAsync(HostScope scope, string email, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of the guests of the org that <paramref name="scope"/> reaches (newest first), excluding deleted ones, optionally
    /// filtered by name, e-mail or phone. Two queries: count and page.
    /// </summary>
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
