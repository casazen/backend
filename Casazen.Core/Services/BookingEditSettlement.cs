using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>What the edit of a booking of the booking site does with the money (E08/F02/US01/T01, NF-75).</summary>
public enum BookingSettlementAction
{
    /// <summary>No money moves online: the price is the same, or it is collected as the booking was agreed (at the property, at the deadline).</summary>
    None = 0,

    /// <summary>The price is lower than what the guest paid: the difference goes back on the Stripe payment(s).</summary>
    Refund = 1,

    /// <summary>The price is higher: the difference is charged, off-session, to the card the booking saved.</summary>
    ChargeSavedCard = 2,

    /// <summary>The price is higher and no card is saved: the guest gets a payment link for the difference.</summary>
    PaymentLink = 3,
}

/// <summary>Where the money of an edit stands once the edit is saved.</summary>
public enum BookingSettlementState
{
    /// <summary>Nothing moved (and nothing was needed).</summary>
    None = 0,

    /// <summary>The refund or the charge is confirmed by Stripe, or the payment link was made and sent.</summary>
    Done = 1,

    /// <summary>Stripe has the refund or the charge and has not confirmed it yet (it is confirmed by its webhook or by the retry job).</summary>
    Pending = 2,
}

/// <summary>
/// What the edit of a booking would do, worked out without doing it (<c>POST /api/bookings/{id}/edit-quote</c>): the new price, what the guest has paid so
/// far, the difference between the two and what happens to it.
/// </summary>
/// <param name="Currency">The currency of the property (ISO code): every amount below is in it.</param>
/// <param name="PreviousTotal">The total of the booking as it is now (tourist tax included).</param>
/// <param name="NewTotal">The total the new dates and guests make it (tourist tax included); the same as <paramref name="PreviousTotal"/> when the edit does not change them.</param>
/// <param name="PaidSoFar">What the guest has paid and not got back: the payments collected minus the refunds made or started, online or recorded by the host.</param>
/// <param name="Difference"><paramref name="NewTotal"/> minus <paramref name="PaidSoFar"/>: below zero the guest paid more than the new price, above zero less.</param>
/// <param name="Action">What happens to the difference.</param>
/// <param name="Amount">The money that moves online: the refund, the charge or the payment link. Zero for <see cref="BookingSettlementAction.None"/>.</param>
/// <param name="OfflineRefundDue">The part of an overpayment that cannot go back online (paid in cash or by transfer, or no longer refundable on Stripe): the host gives it back.</param>
public sealed record BookingEditQuote(
    string Currency,
    decimal PreviousTotal,
    decimal NewTotal,
    decimal PaidSoFar,
    decimal Difference,
    BookingSettlementAction Action,
    decimal Amount,
    decimal OfflineRefundDue);

/// <summary>The money of an edit that was saved (<c>PUT /api/bookings/{id}</c>, the <c>settlement</c> block).</summary>
/// <param name="Action">What was done with the difference.</param>
/// <param name="State">Where it stands.</param>
/// <param name="Amount">The money that moved online (refunded, charged or asked with the link); zero when none did.</param>
/// <param name="OfflineRefundDue">What the host still has to give back outside CasaZen.</param>
/// <param name="Currency">The currency of the property (ISO code).</param>
public sealed record BookingEditSettlement(
    BookingSettlementAction Action,
    BookingSettlementState State,
    decimal Amount,
    decimal OfflineRefundDue,
    string Currency)
{
    /// <summary>An edit that moved no money.</summary>
    public static BookingEditSettlement NotNeeded(string currency) =>
        new(BookingSettlementAction.None, BookingSettlementState.None, 0m, 0m, currency);
}

/// <summary>The booking as the edit left it and what happened to the money (<see cref="IHostBookingService.UpdateAsync"/>).</summary>
public sealed record HostBookingUpdateResult(Booking Booking, BookingEditSettlement Settlement);

/// <summary>A Stripe payment of a booking and how much of it can still be refunded online.</summary>
public sealed record BookingRefundableShare(Guid PaymentId, decimal Refundable);

/// <summary>One refund of an edit: <see cref="Amount"/> on the payment <see cref="PaymentId"/>.</summary>
public sealed record BookingRefundPart(Guid PaymentId, decimal Amount);

/// <summary>What the money of an edit comes to (<see cref="BookingEditSettlementRules.Plan"/>).</summary>
/// <param name="Action">What happens to the difference.</param>
/// <param name="Amount">The money that moves online.</param>
/// <param name="OfflineRefundDue">The overpayment that cannot go back online.</param>
/// <param name="Refunds">For <see cref="BookingSettlementAction.Refund"/>: the refund of each payment, newest payment first.</param>
public sealed record BookingEditPlan(
    BookingSettlementAction Action,
    decimal Amount,
    decimal OfflineRefundDue,
    IReadOnlyList<BookingRefundPart> Refunds)
{
    /// <summary>No money moves.</summary>
    public static BookingEditPlan None { get; } = new(BookingSettlementAction.None, 0m, 0m, []);
}

/// <summary>
/// The rules of the money of an edit (E08/F02/US01/T01, NF-75), pure: the difference between the new total and what the guest has paid is refunded
/// when it is negative, collected when it is positive, and left alone when it is zero. Every amount is to the cent.
/// </summary>
public static class BookingEditSettlementRules
{
    /// <summary>The idempotency key of the refund of one payment in the edit of a booking at <paramref name="updatedAt"/>: the same for the same edit, so a retried request returns the refund Stripe already made.</summary>
    public static string RefundKey(Guid bookingId, DateTime updatedAt, Guid paymentId) =>
        $"booking-edit-refund:{bookingId}:{updatedAt:O}:{paymentId:N}";

    /// <summary>The idempotency key of the charge of the saved card in the edit of a booking at <paramref name="updatedAt"/>, for the amount <paramref name="amount"/>.</summary>
    public static string ChargeKey(Guid bookingId, DateTime updatedAt, decimal amount) =>
        $"booking-edit-charge:{bookingId}:{updatedAt:O}:{(long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero)}";

    /// <summary>
    /// The money of an edit. <paramref name="paid"/> is what the guest has paid and not got back (online and offline); <paramref name="refundableNewestFirst"/> are
    /// the Stripe payments with what can still go back on each, newest first; <paramref name="collectsOnline"/> says a higher price is collected now, online
    /// (the guest paid online already; not for a booking paid at the property, or whose card is charged at its deadline); <paramref name="hasSavedCard"/> says
    /// the booking has a card to charge.
    /// </summary>
    public static BookingEditPlan Plan(
        decimal newTotal,
        decimal paid,
        IEnumerable<BookingRefundableShare> refundableNewestFirst,
        bool collectsOnline,
        bool hasSavedCard)
    {
        ArgumentNullException.ThrowIfNull(refundableNewestFirst);

        var difference = Cents(newTotal - paid);
        if (difference == 0m)
            return BookingEditPlan.None;

        if (difference > 0m)
        {
            if (!collectsOnline)
                return BookingEditPlan.None;

            return new BookingEditPlan(
                hasSavedCard ? BookingSettlementAction.ChargeSavedCard : BookingSettlementAction.PaymentLink,
                difference,
                0m,
                []);
        }

        // The guest paid more than the new price: the overpayment goes back on the Stripe payments, never above what each can still return.
        var overpaid = -difference;
        var remaining = overpaid;
        var parts = new List<BookingRefundPart>();
        foreach (var share in refundableNewestFirst)
        {
            if (remaining <= 0m)
                break;

            var part = Math.Min(remaining, Cents(share.Refundable));
            if (part <= 0m)
                continue;

            parts.Add(new BookingRefundPart(share.PaymentId, part));
            remaining = Cents(remaining - part);
        }

        var online = Cents(overpaid - remaining);
        return new BookingEditPlan(online > 0m ? BookingSettlementAction.Refund : BookingSettlementAction.None, online, remaining, parts);
    }

    /// <summary>An amount to the cent (half away from zero, as every amount of CasaZen).</summary>
    public static decimal Cents(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
