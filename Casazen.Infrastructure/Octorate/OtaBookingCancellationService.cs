using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Octorate;

/// <summary>
/// <see cref="IOtaBookingCancellationService"/> (E08/F02/US02/T02, D8, NF-76): the cancellation of a booking that came from a channel, Octorate
/// first and CasaZen after.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The order is the point.</b> The booking is cancelled in CasaZen only after Octorate confirmed the cancellation of its reservation.
/// Whatever stops the request before that (the channel is closed, the connector is off, the property is not mirrored or its mapping is
/// write-blocked, Octorate refuses or does not answer) leaves the booking exactly as it was and answers with the sentence that tells how to ask
/// the guest or the channel (<see cref="OtaChannelActionPolicy"/>): never a success Octorate did not confirm.</item>
/// <item><b>Closed by default.</b> A channel the configuration does not open is refused with 422 <c>ota_booking_cancel_not_allowed</c> before
/// Octorate is asked (D8 is unconfirmed for every channel).</item>
/// <item><b>Where.</b> The target of <see cref="IOctorateBookingTargets"/>, from the confirmed mapping of the property: a property whose mapping
/// <see cref="IChannelMappingService.IsWriteBlockedAsync"/> blocks, or that is not mirrored, is never written to (422 <c>octorate_cancel_failed</c>).</item>
/// <item><b>Once.</b> Under the advisory lock of the cancellation of the booking, the one the Stripe cancellation and the host's changes take: a
/// double click waits and then answers 409 <c>booking_already_cancelled</c>. The booking is read again under the lock. A cancellation whose answer
/// was lost and is asked again finds the reservation cancelled (the client reads it first) and succeeds.</item>
/// <item><b>No echo.</b> <c>BookingCancelled</c> is recorded as <see cref="BookingEventOrigin.Octorate"/>, so the push of the cancellations CasaZen
/// makes (<see cref="OctorateBookingCancelHandler"/>) skips it, and the webhook of Octorate that follows finds the booking cancelled and changes
/// nothing.</item>
/// <item><b>No money.</b> The channel collects its own bookings: no refund, no PaymentIntent, no e-mail to the guest.</item>
/// <item><b>Logs</b> carry ids, the channel and codes: never a name, a note or a body.</item>
/// </list>
/// </remarks>
public sealed class OtaBookingCancellationService(
    AppDbContext db,
    IOctorateApiClient client,
    IOctorateOrgScope orgScope,
    IOctorateBookingTargets targets,
    OtaChannelActionPolicy policy,
    IDomainEventRecorder recorder,
    IFeatureFlags featureFlags,
    TimeProvider timeProvider,
    ILogger<OtaBookingCancellationService> logger) : IOtaBookingCancellationService
{
    /// <summary>The longest note kept on a cancelled booking (the column holds 500).</summary>
    private const int NoteMaxLength = 500;

    public async Task<Booking> CancelAsync(Guid bookingId, string? reason, CancellationToken ct)
    {
        // The booking as the caller sees it: through the tenant filter, so a booking of another org is not found.
        var seen = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.Source, b.OctorateReservationId })
            .FirstOrDefaultAsync(ct)
            ?? throw BookingNotFound(bookingId);
        if (!OtaChannelActionPolicy.IsChannelBooking(seen.Source, seen.OctorateReservationId))
        {
            throw new InvalidOperationException(
                $"Booking {bookingId} did not come from a channel: it is cancelled by IBookingCancellationService, not by Octorate first.");
        }

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db, ct, (PostgresAdvisoryLocks.Scope.BookingCancellation, bookingId.ToString("N")));

        // Whatever was read before the lock may be old by now: a click that was first has cancelled the booking, the import has applied the
        // cancellation of the channel. Read again, under the lock, from what is committed.
        db.ChangeTracker.Clear();
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct) ?? throw BookingNotFound(bookingId);
        if (booking.Status == BookingStatus.Cancelled)
            throw new DomainConflictException(BookingErrorCodes.AlreadyCancelled, "BookingAlreadyCancelled");

        if (!BookingEditRules.IsCancellable(booking.Status))
            throw new DomainRuleException(BookingErrorCodes.NotCancellable, "BookingNotCancellable");

        if (!OtaChannelActionPolicy.IsChannelBooking(booking))
        {
            throw new InvalidOperationException(
                $"Booking {bookingId} did not come from a channel: it is cancelled by IBookingCancellationService, not by Octorate first.");
        }

        // The rule of the channel, closed unless the configuration opens it: Octorate is not even asked and nothing changes.
        if (!policy.For(booking.Source).CanCancel)
            throw policy.CancelNotAllowed(booking.Source);

        // Octorate first. Nothing below changes the booking until it has confirmed.
        await AskOctorateAsync(booking, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = now;
        booking.CancellationNote = Note(reason);
        booking.UpdatedAt = now;

        // A change that came from the channel manager as far as the consumers are concerned: the push of CasaZen's own cancellations skips it.
        await BookingEvents.RecordAsync(recorder, db, DomainEventType.BookingCancelled, booking, BookingEventOrigin.Octorate, ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        logger.LogInformation(
            "Booking {BookingId} of org {OrgId} ({Channel}) cancelled: Octorate confirmed the cancellation of its reservation",
            booking.Id,
            booking.OrgId,
            booking.Source);
        return booking;
    }

    /// <summary>
    /// Asks Octorate to cancel the reservation of <paramref name="booking"/> and returns only when it confirmed. Every other end throws and leaves
    /// the booking alone: 422 <c>octorate_cancel_failed</c> when Octorate cannot be asked or refuses, <see cref="OtaCancellationUnavailableException"/>
    /// (503) when it does not answer.
    /// </summary>
    private async Task AskOctorateAsync(Booking booking, CancellationToken ct)
    {
        var rule = policy.For(booking.Source);

        if (!featureFlags.IsEnabled(FeatureFlags.OctorateConnector))
        {
            logger.LogInformation(
                "Booking {BookingId} of org {OrgId}: the Octorate connector is off, the cancellation of its reservation is not asked",
                booking.Id,
                booking.OrgId);
            throw policy.CancelFailed(booking.Source);
        }

        OctorateTargetLookup lookup;
        try
        {
            lookup = await targets.LookupAsync(booking.PropertyId, ct);
        }
        catch (OctorateException ex)
        {
            throw Unconfirmed(booking, rule, ex);
        }

        // Not mirrored, a mapping that is write-blocked or still to decide, a room not chosen: a property CasaZen never writes to.
        if (lookup.Target is not { } target)
        {
            logger.LogInformation(
                "Booking {BookingId} of org {OrgId}: its property has no target in Octorate ({Gap}), the cancellation of its reservation is not asked",
                booking.Id,
                booking.OrgId,
                lookup.Gap);
            throw policy.CancelFailed(booking.Source);
        }

        try
        {
            using (orgScope.Use(booking.OrgId))
                await client.CancelReservationAsync(target, booking.OctorateReservationId!, ct);
        }
        catch (OctorateException ex)
        {
            throw Unconfirmed(booking, rule, ex);
        }
        catch (ArgumentException ex)
        {
            // An id Octorate could not take: nothing left CasaZen, nothing was confirmed.
            logger.LogWarning(ex, "Booking {BookingId} of org {OrgId}: the cancellation of its reservation could not be written, not sent", booking.Id, booking.OrgId);
            throw policy.CancelFailed(booking.Source);
        }
    }

    /// <summary>
    /// The end of a cancellation Octorate did not confirm: no answer, 429 or 5xx is "unreachable" (503), anything else (a refusal, a conflict, the
    /// reservation not found, refused credentials, an org not connected) is a refusal (422). The booking is as it was in both.
    /// </summary>
    private Exception Unconfirmed(Booking booking, OtaChannelActionRule rule, OctorateException error)
    {
        var unreachable = error is OctorateServerException or OctorateRateLimitException;
        logger.LogWarning(
            "Booking {BookingId} of org {OrgId} ({Channel}): Octorate did not confirm the cancellation ({Code}, HTTP {Status}); the booking is unchanged",
            booking.Id,
            booking.OrgId,
            booking.Source,
            error.Code,
            error.OctorateStatusCode);

        return unreachable
            ? new OtaCancellationUnavailableException(booking.Source, rule.CancelGuidanceKey, error)
            : policy.CancelFailed(booking.Source);
    }

    private static string? Note(string? reason)
    {
        var note = reason?.Trim();
        if (string.IsNullOrEmpty(note))
            return null;

        return note.Length <= NoteMaxLength ? note : note[..NoteMaxLength];
    }

    private static NotFoundException BookingNotFound(Guid bookingId) =>
        new($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
}
