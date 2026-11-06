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
    /// Octorate did not answer (or answered 429 or 5xx) and the reservation was not found by its reference, or the rooms of the target could not
    /// be read: the sync is <c>Failed</c> with <c>octorate_unavailable</c> and the exception propagates, so the outbox dispatcher delivers the
    /// event again (ADR-022-R4, R5). The same when the reservation exists but a payment of the booking could not be recorded on it: the sync is
    /// <c>Failed</c> with <c>octorate_payments_not_recorded</c> and the exception propagates; the delivery that follows records what is left.
    /// </exception>
    Task<OctorateSyncState?> PushCreateAsync(Guid bookingId, CancellationToken ct);

    /// <summary>
    /// Asks the push of a booking of the caller's org again (<c>POST /api/bookings/{id}/octorate-sync/retry</c>): only a <c>Failed</c> or
    /// <c>Conflict</c> sync; the push is queued through the event bus and the answer is <c>Pending</c>. The row itself keeps its state until the
    /// push really starts (it is then <c>Pending</c>), so a push that gives up (the booking was cancelled, its stay is over, the property is no
    /// longer mirrored) leaves a row the host can ask again. <paramref name="overwrite"/> is the host's choice to write CasaZen's version over a
    /// reservation that changed on Octorate (a change of E08/F02); a create has nothing to overwrite.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist or is of another org (404).</exception>
    /// <exception cref="Exceptions.DomainConflictException">409 <see cref="OctorateSyncErrorCodes.NotRetryable"/>.</exception>
    Task<OctorateSyncState> RequeueAsync(Guid bookingId, bool overwrite, CancellationToken ct);

    /// <summary>
    /// Records a paid payment of a mirrored booking on its Octorate reservation, once (E08/F01/US01/T04, AC-NF74-4, D9): income, "not a card
    /// charge", with the Stripe PaymentIntent id (or <c>casazen-{paymentId}</c> for a payment the host recorded) as <c>transaction</c>, the
    /// configured <c>paymentMode</c> and the amount without the part that is tourist tax (Octorate works the tax out itself). Nothing for a
    /// booking that is not mirrored, of a channel (the channel collects), whose property is write-blocked, or a payment already recorded. A
    /// payment of a booking whose reservation is being created, or is about to be (its confirmation still waits in the outbox: the booking site
    /// records the payment first), makes the event be delivered again (<c>OctorateSyncPendingException</c>); the create also records the
    /// payments paid before it succeeded. On a mirror marked <c>Failed</c> because a payment could not be recorded, the event records every
    /// payment still to be recorded and closes the mirror, like the confirmation does: it is never consumed with no effect.
    /// </summary>
    Task PushPaymentAsync(Guid paymentId, CancellationToken ct);
}
