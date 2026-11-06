using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// The cancellation of a booking that came from a channel through the channel manager (E08/F02/US02/T02, D8, NF-76): <b>Octorate first, CasaZen
/// after</b>. The booking is cancelled in CasaZen only when Octorate confirmed the cancellation of its reservation; when Octorate refuses, cannot
/// be reached, or the channel does not let the host cancel from CasaZen, the booking is exactly as it was and the answer says so, with the one
/// sentence that tells how to ask the guest or the channel. It is the opposite order of the booking CasaZen holds (a direct or a manual one):
/// that one is cancelled in CasaZen, with its Stripe refund, and Octorate follows (<c>OctorateBookingCancelHandler</c>).
/// </summary>
/// <remarks>
/// The caller authorizes the booking first: the service never checks roles. It moves no money: the channel collects its own bookings, so there is
/// no refund and no PaymentIntent here (<see cref="IBookingCancellationService"/> refuses a channel booking).
/// </remarks>
public interface IOtaBookingCancellationService
{
    /// <summary>
    /// Asks Octorate to cancel the reservation of the booking and, only when it confirmed, sets the booking <see cref="BookingStatus.Cancelled"/>
    /// (<c>CancelledAt</c>, the host's <paramref name="reason"/> kept as the note, never sent to the guest) and records <c>BookingCancelled</c>
    /// as a change that came from the channel manager (<see cref="Events.BookingEventOrigin.Octorate"/>), so the push of the cancellations that
    /// CasaZen makes skips it. Returns the cancelled booking.
    /// </summary>
    /// <exception cref="Exceptions.NotFoundException">The booking does not exist, or is of another org.</exception>
    /// <exception cref="Exceptions.DomainConflictException"><see cref="BookingErrorCodes.AlreadyCancelled"/>.</exception>
    /// <exception cref="Exceptions.DomainRuleException">
    /// <see cref="BookingErrorCodes.NotCancellable"/> (checked out), <see cref="BookingErrorCodes.OtaBookingCancelNotAllowed"/> (the channel is
    /// closed: nothing is asked of Octorate) or <see cref="BookingErrorCodes.OctorateCancelFailed"/> (Octorate refused, or the property cannot be
    /// reached through a connected, confirmed mapping): the booking is unchanged.
    /// </exception>
    /// <exception cref="OtaCancellationUnavailableException">Octorate did not answer (or answered 429 or 5xx): the booking is unchanged.</exception>
    /// <exception cref="InvalidOperationException">The booking did not come from a channel: it is cancelled by <see cref="IBookingCancellationService"/>.</exception>
    Task<Booking> CancelAsync(Guid bookingId, string? reason, CancellationToken ct);
}

/// <summary>
/// Octorate could not be reached to cancel the reservation of a channel booking (it did not answer, or answered 429 or 5xx): the booking is
/// unchanged, and the API answers 503 with the sentence that tells how to ask on the channel (E08/F02/US02/T02).
/// </summary>
public sealed class OtaCancellationUnavailableException : Exception
{
    public OtaCancellationUnavailableException(BookingSource source, string guidanceKey, Exception? innerException = null)
        : base($"Octorate could not be reached to cancel a booking of the channel {source}.", innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guidanceKey);
        Channel = source.ToString();
        GuidanceKey = guidanceKey;
    }

    /// <summary>The channel of the booking (<c>BookingCom</c>).</summary>
    public string Channel { get; }

    /// <summary>The key of <c>SharedResources</c> of the sentence that tells how to ask the guest or the channel.</summary>
    public string GuidanceKey { get; }
}
