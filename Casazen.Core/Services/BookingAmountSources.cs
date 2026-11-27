using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>
/// Where a figure of a booking comes from (E08/F03/US03/T02, NF-77, AC-NF77-4, AC-NF77-5): the words a screen turns into «Stripe», «channel
/// manager» and «entered by you», never told by colour alone.
/// </summary>
public enum AmountSource
{
    /// <summary>The booking site: the price the guest was quoted and the card payments Stripe collected for it (a direct booking).</summary>
    Stripe,

    /// <summary>The channel manager: the figures of a booking that came from a channel, or from the booking engine of the agency, are the ones Octorate gives.</summary>
    ChannelManager,

    /// <summary>The host: a price, or a payment (cash, bank transfer), that the host entered by hand.</summary>
    Manual,
}

/// <summary>
/// The source of each figure of a booking (<see cref="BookingAmountSourceRules.Of"/>); a figure that is <c>null</c> here is a figure the booking
/// does not have (not available, or nothing to attribute), so a screen shows no source for it.
/// </summary>
/// <param name="Total">The price of the stay.</param>
/// <param name="Paid">What came in.</param>
/// <param name="Balance">What is still to pay (the money of the channel manager only: CasaZen does not work out a balance of its own bookings).</param>
/// <param name="Commission">What the channel kept (the money of the channel manager only).</param>
/// <param name="Net">What the host receives (the money of the channel manager only).</param>
public sealed record BookingAmountSources(
    AmountSource? Total,
    AmountSource? Paid,
    AmountSource? Balance,
    AmountSource? Commission,
    AmountSource? Net);

/// <summary>
/// The one rule of where the figures of a booking come from (NF-77). When the money of a booking is the channel manager's
/// (<see cref="HoldsChannelManagerMoney"/>: a booking of a channel, or a reservation of the booking engine of the agency that the import made) it has the
/// figures of Octorate and nothing else: each one is <see cref="AmountSource.ChannelManager"/> when Octorate gave it, and has no source when it did
/// not (a figure that is not available is not attributed). Any other booking is CasaZen's: its price is <see cref="AmountSource.Stripe"/> when it is the
/// quote of the booking site and <see cref="AmountSource.Manual"/> when the host entered the booking or the price; what came in is
/// <see cref="AmountSource.Stripe"/> when every collected payment went through Stripe and <see cref="AmountSource.Manual"/> as soon as one of them is a
/// cash or bank transfer payment the host recorded: a number that holds something the host typed is never presented as Stripe's.
/// </summary>
public static class BookingAmountSourceRules
{
    /// <summary>
    /// Whether the figures of <paramref name="booking"/> are those of the channel manager. They are for a booking of a channel
    /// (<see cref="OtaChannelActionPolicy.IsChannelBooking(Booking)"/>) and for a reservation of the booking engine of the agency (channel 270, counted as
    /// <see cref="BookingSource.Direct"/> for the badge, the fiscal rules and the changes) that the import made: it has a reservation on Octorate whose prices
    /// and amounts are Octorate's. The two differ from a booking CasaZen made and sent to Octorate, which has a reservation there too and whose money is
    /// CasaZen's, by the row of the mirror (<see cref="OctorateBookingSync"/>): only the push of CasaZen makes it, never the import. Hence
    /// <paramref name="pushedByCasaZen"/>: the booking has that row.
    /// </summary>
    public static bool HoldsChannelManagerMoney(Booking booking, bool pushedByCasaZen)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return OtaChannelActionPolicy.IsChannelBooking(booking)
               || (booking.Source == BookingSource.Direct
                   && !pushedByCasaZen
                   && !string.IsNullOrWhiteSpace(booking.OctorateReservationId));
    }

    /// <summary>
    /// The sources of the figures of <paramref name="booking"/>, whose payments are <paramref name="payments"/>; <paramref name="pushedByCasaZen"/> is
    /// whether it has a row of the mirror on Octorate (see <see cref="HoldsChannelManagerMoney"/>).
    /// </summary>
    public static BookingAmountSources Of(Booking booking, IEnumerable<Payment> payments, bool pushedByCasaZen)
    {
        ArgumentNullException.ThrowIfNull(booking);
        ArgumentNullException.ThrowIfNull(payments);

        if (HoldsChannelManagerMoney(booking, pushedByCasaZen))
        {
            return new BookingAmountSources(
                AmountSource.ChannelManager,
                FromChannel(booking.OtaPaidAmount),
                FromChannel(booking.OtaBalanceAmount),
                FromChannel(booking.OtaCommission),
                FromChannel(booking.OtaNetAmount));
        }

        // Only a booking made on the booking site, at the price of its quote, is priced there (one CasaZen sent to Octorate included: its price stays
        // CasaZen's); every other one (entered by the host, a stay from an iCal feed) has the price the host gave it.
        var total = booking.Source == BookingSource.Direct && !booking.PriceOverridden ? AmountSource.Stripe : AmountSource.Manual;
        return new BookingAmountSources(total, PaidSourceOf(payments), null, null, null);
    }

    /// <summary>
    /// Where what came in comes from: the collected payments (completed or partly refunded, as <see cref="BookingListRules.PaidAmount"/> counts them)
    /// that still hold money. <c>null</c> when there is none, so a booking with nothing collected shows a zero with no source.
    /// </summary>
    public static AmountSource? PaidSourceOf(IEnumerable<Payment> payments)
    {
        ArgumentNullException.ThrowIfNull(payments);

        var collected = payments
            .Where(p => p.Status is PaymentStatus.Completed or PaymentStatus.PartiallyRefunded
                        && PaymentCashRules.Collected(p.Amount, p.RefundedAmount) > 0m)
            .ToList();
        if (collected.Count == 0)
            return null;

        return collected.Any(IsEnteredByHand) ? AmountSource.Manual : AmountSource.Stripe;
    }

    /// <summary>
    /// A payment the host recorded by hand: cash or a bank transfer that Stripe never saw. A payment with a PaymentIntent went through Stripe,
    /// whatever its method says (the same precedence as the record sent to the channel manager).
    /// </summary>
    public static bool IsEnteredByHand(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);

        return string.IsNullOrWhiteSpace(payment.StripePaymentIntentId)
               && payment.Method is PaymentMethod.CashOnArrival or PaymentMethod.BankTransfer;
    }

    private static AmountSource? FromChannel(decimal? figure) => figure is null ? null : AmountSource.ChannelManager;
}
