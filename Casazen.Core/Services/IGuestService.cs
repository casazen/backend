using Casazen.Core.Authorization;
using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Guest records of an org (TN-1). Every read and write is scoped to the caller's org: a guest of
/// another org is reported as not found and its existence is never revealed. The reads, the correction and the deletion of
/// a guest take the caller's <see cref="HostScope"/> (SR-04b): a caller limited to some properties reaches only the guests
/// that have a stay at one of them, and any other guest is reported as not found too.
/// </summary>
public interface IGuestService
{
    Task<Guest?> GetGuestAsync(HostScope scope, Guid id, CancellationToken cancellationToken = default);
    Task<Guest?> GetGuestByEmailAsync(HostScope scope, string email, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Guest> Items, int TotalCount)> GetGuestsPageAsync(
        HostScope scope,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<Guest> CreateGuestAsync(Guid orgId, Guest guest, CancellationToken cancellationToken = default);

    Task<Guest> CreateGuestSnapshotAsync(Guest guest);

    Task<Guest> UpdateGuestAsync(Guest guest);

    /// <summary>
    /// Deletes a guest of the org of <paramref name="scope"/> that the scope reaches: hard delete when nothing references it,
    /// otherwise soft delete with anonymization (same as the GDPR erasure). Throws <c>NotFoundException</c> when
    /// the guest is not in the org or outside the scope and <c>DomainConflictException</c> (<c>guest_has_open_bookings</c>)
    /// while one of its bookings is still open.
    /// </summary>
    /// <remarks><paramref name="actorUserId"/> is the host who asked, recorded in the GDPR audit (CO-15).</remarks>
    Task<GuestDeletionResult> DeleteGuestAsync(
        HostScope scope,
        Guid id,
        string? actorUserId = null,
        CancellationToken cancellationToken = default);
}

public enum GuestDeletionResult
{
    Deleted,

    Anonymized,
}
