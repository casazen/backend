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
    /// caller. Dates and guests change only for a booking entered by the host (<see cref="BookingSource.Manual"/>) or a
    /// confirmed one of the booking site (<see cref="BookingSource.Direct"/>) before its check-in; new dates are checked against
    /// the other bookings and the imported calendar blocks (409 <see cref="BookingErrorCodes.DatesUnavailable"/>) and the price,
    /// tourist tax included, is computed again.
    /// </summary>
    /// <remarks>
    /// For a booking of the booking site the money follows the price (E08/F02/US01/T01, NF-75): against what the guest has paid
    /// (<see cref="BookingEditQuote.PaidSoFar"/>), a lower price is refunded on the Stripe payments, newest first, never above what
    /// each can still return, with the idempotency key <c>booking-edit-refund:{bookingId}:{UpdatedAt:O}</c> (plus the payment); a higher
    /// one is charged to the card the booking saved, else the guest is sent a payment link for the difference; the same price moves
    /// nothing. What was paid offline cannot go back online and is reported (<see cref="BookingEditSettlement.OfflineRefundDue"/>). The Stripe
    /// call comes before the save and in the same unit of work: when Stripe refuses, nothing is saved (422
    /// <see cref="BookingErrorCodes.SettlementFailed"/>), and the booking change and the refund row are committed together. After the
    /// commit the guest is told by email; a failed email never undoes the edit.
    /// </remarks>
    Task<HostBookingUpdateResult> UpdateAsync(HostBookingUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// What <see cref="UpdateAsync"/> would do with <paramref name="update"/>, worked out and not done (<c>POST /api/bookings/{id}/edit-quote</c>):
    /// the new total, what the guest has paid, the difference and the action on it. The same checks as the edit (a booking that cannot be changed,
    /// dates that are not free, guests over the capacity, ages of the minors that are missing) answer the same errors; nothing is locked or written.
    /// </summary>
    Task<BookingEditQuote> QuoteEditAsync(HostBookingUpdate update, CancellationToken cancellationToken = default);

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
/// <param name="OtaAmounts">
/// The channel amounts the request carried, if any. They are never applied: a figure that differs from the stored one is refused with 422
/// <see cref="BookingErrorCodes.OtaAmountsReadonly"/> (E02/F02/US01/T01, ADR-033-R3).
/// </param>
/// <param name="RequestedByUserId">The Auth0 subject of the host, kept on the refunds of the edit; <c>null</c> when there is none.</param>
public sealed record HostBookingUpdate(
    Guid BookingId,
    DateTime CheckInDate,
    DateTime CheckOutDate,
    int NumberOfGuests,
    int NumberOfChildren,
    IReadOnlyList<int>? ChildrenAges,
    string? SpecialRequests,
    OtaAmountsInput? OtaAmounts = null,
    string? RequestedByUserId = null);

/// <summary>
/// The commission, net, paid and balance amounts of the channel as a request to change a booking carried them (each <c>null</c> when it did
/// not). Read-only after the import: they come from the channel manager.
/// </summary>
public sealed record OtaAmountsInput(decimal? Commission, decimal? Net, decimal? Paid, decimal? Balance)
{
    /// <summary>True when a figure was given that is not the one in <paramref name="stored"/> (the four figures the booking holds): that would be an edit.</summary>
    public bool Edits(OtaAmountsInput stored)
    {
        ArgumentNullException.ThrowIfNull(stored);

        return (Commission is not null && Commission != stored.Commission)
               || (Net is not null && Net != stored.Net)
               || (Paid is not null && Paid != stored.Paid)
               || (Balance is not null && Balance != stored.Balance);
    }
}
