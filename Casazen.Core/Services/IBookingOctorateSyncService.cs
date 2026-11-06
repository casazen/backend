using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The mirror of the CasaZen bookings on Octorate (E08/F01/US01/T03, ADR-031, NF-74): a booking confirmed in CasaZen on a property whose
/// mapping is confirmed is created in Octorate once, asynchronously (the handler of <c>BookingConfirmed</c>), with a state the host sees on the
/// booking (<c>OctorateBookingSync</c>). The host booking, the phone entry and the booking site follow the same path.
/// </summary>
public interface IBookingOctorateSyncService
{
    /// <summary>
    /// Creates the reservation of a confirmed booking on Octorate, once (see the remarks of the implementation): the state of the sync after
    /// the attempt, or <c>null</c> when the booking is not one CasaZen mirrors (a channel's booking, one that came from Octorate, not confirmed, a
    /// stay that is over, a property that is not mirrored): then nothing is written, not even a sync row.
    /// </summary>
    /// <exception cref="Exception">
    /// Octorate did not answer (or answered 429 or 5xx) and the reservation was not found by its reference: the sync is <c>Failed</c> with
    /// <c>octorate_unavailable</c> and the exception propagates, so the outbox dispatcher delivers the event again (ADR-022-R4, R5).
    /// </exception>
    Task<OctorateSyncState?> PushCreateAsync(Guid bookingId, CancellationToken ct);

    /// <summary>
    /// Asks the push of a booking of the caller's org again (<c>POST /api/bookings/{id}/octorate-sync/retry</c>): only a <c>Failed</c> or
    /// <c>Conflict</c> sync; it becomes <c>Pending</c> and the push runs again through the event bus. <paramref name="overwrite"/> is the host's
    /// choice to write CasaZen's version over a reservation that changed on Octorate (a change of E08/F02); a create has nothing to overwrite.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist or is of another org (404).</exception>
    /// <exception cref="Exceptions.DomainConflictException">409 <see cref="OctorateSyncErrorCodes.NotRetryable"/>.</exception>
    Task<OctorateSyncState> RequeueAsync(Guid bookingId, bool overwrite, CancellationToken ct);
}
