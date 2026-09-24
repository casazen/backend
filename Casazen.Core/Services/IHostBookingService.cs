using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Changes the host makes to an existing booking from the console (PC-07, A2-07, A2-08): edit of the fields the host
/// owns, confirmation of a pending booking entered by hand. Cancellation (refunds and intents on Stripe) is
/// <see cref="IBookingCancellationService"/>; arrival and check-out are <see cref="IStayLifecycleService"/> (CO-08). Each change runs under the lock of the booking that the cancellation
/// takes, so a change and a cancellation sent together never overwrite each other. Callers authorize the booking first:
/// the service never checks roles. Errors are <see cref="Exceptions.DomainRuleException"/> (422),
/// <see cref="Exceptions.DomainConflictException"/> (409) and <see cref="Exceptions.NotFoundException"/> with the codes
/// of <see cref="BookingErrorCodes"/>.
/// </summary>
public interface IHostBookingService
{
    /// <summary>
    /// Applies <paramref name="update"/>. Status, source, prices, guest and payment fields are never taken from the
    /// caller. Dates and guests change only for a booking entered by the host (<see cref="BookingSource.Manual"/>)
    /// before its check-in; new dates are checked against the other bookings and the imported calendar blocks (409
    /// <see cref="BookingErrorCodes.DatesUnavailable"/>) and the price, tourist tax included, is computed again.
    /// </summary>
    Task<Booking> UpdateAsync(HostBookingUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// The host confirms a <see cref="BookingStatus.Pending"/> booking: the single confirmation of the console
    /// (<c>POST /api/bookings/{id}/approve</c>).
    /// <list type="bullet">
    /// <item>a booking entered by the host (<see cref="BookingSource.Manual"/>; the old code stored them Pending and PC-01
    /// marked them Manual without confirming them): its dates must still be free;</item>
    /// <item>a "pay at the property" request (decision D5): accepted through
    /// <see cref="IOnSiteBookingRequestService.AcceptAsync"/> (BK-06), with its rules and emails;</item>
    /// <item>any other pending booking (a checkout hold waiting for the guest's payment) cannot be confirmed by the host:
    /// 422 <see cref="BookingErrorCodes.NotConfirmable"/>.</item>
    /// </list>
    /// </summary>
    Task<Booking> ConfirmAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

/// <summary>What the host may change on a booking.</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="CheckInDate">Check-in day.</param>
/// <param name="CheckOutDate">Check-out day.</param>
/// <param name="NumberOfGuests">All guests, minors included.</param>
/// <param name="NumberOfChildren">Minors among <paramref name="NumberOfGuests"/> (under 18).</param>
/// <param name="ChildrenAges">Age of each minor at check-in, when the tourist tax of the comune depends on it.</param>
/// <param name="SpecialRequests">Notes of the booking.</param>
public sealed record HostBookingUpdate(
    Guid BookingId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int NumberOfGuests,
    int NumberOfChildren,
    IReadOnlyList<int>? ChildrenAges,
    string? SpecialRequests);
