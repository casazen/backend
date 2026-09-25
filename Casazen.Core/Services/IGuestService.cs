using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Guest records of an org (TN-1). Every read and write is scoped to the caller's org: a guest of
/// another org is reported as not found and its existence is never revealed.
/// </summary>
public interface IGuestService
{
    Task<Guest?> GetGuestAsync(Guid orgId, Guid id, CancellationToken cancellationToken = default);
    Task<Guest?> GetGuestByEmailAsync(Guid orgId, string email, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Guest> Items, int TotalCount)> GetGuestsPageAsync(
        Guid orgId,
        string? searchTerm,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a guest in <paramref name="orgId"/>. Throws <c>DomainConflictException</c>
    /// (<c>guest_email_exists</c>) only when the same org already has a guest with that e-mail.
    /// </summary>
    Task<Guest> CreateGuestAsync(Guid orgId, Guest guest, CancellationToken cancellationToken = default);

    /// <summary>Stores a per-booking guest snapshot; <see cref="Guest.OrgId"/> must be set.</summary>
    Task<Guest> CreateGuestSnapshotAsync(Guest guest);

    Task<Guest> UpdateGuestAsync(Guest guest);

    /// <summary>
    /// Deletes a guest of <paramref name="orgId"/>: hard delete when nothing references it, otherwise
    /// soft delete with anonymization (same as the GDPR erasure). Throws <c>NotFoundException</c> when
    /// the guest is not in the org and <c>DomainConflictException</c> (<c>guest_has_open_bookings</c>)
    /// while one of its bookings is still open.
    /// </summary>
    /// <remarks><paramref name="actorUserId"/> is the host who asked, recorded in the GDPR audit (CO-15).</remarks>
    Task<GuestDeletionResult> DeleteGuestAsync(
        Guid orgId,
        Guid id,
        string? actorUserId = null,
        CancellationToken cancellationToken = default);
}

public enum GuestDeletionResult
{
    /// <summary>The row was removed: no booking or Alloggiati report referenced it.</summary>
    Deleted,

    /// <summary>The row is kept for its bookings, marked deleted and anonymized.</summary>
    Anonymized,
}
