using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.TouristTax;

public enum TouristTaxNotPayableReason
{
    None = 0,

    StayNotActive = 1,

    PaidAtProperty = 2,

    ChannelCollects = 3,

    CollectorUnknown = 4,

    NotComputable = 5,

    AwaitingConfirmation = 6,

    NothingDue = 7,

    AlreadyPaidOnline = 8,
}

/// <summary>What the collection of the tourist tax of a stay is decided from (<see cref="TouristTaxCollectionRules.Evaluate"/>). Amounts in the currency of the property.</summary>
/// <param name="StayActive">The stay owes the tax: <see cref="AlloggiatiStatusRules.IsDue"/> (not a request, a hold, a cancelled stay or a no-show).</param>
/// <param name="EndedBeforeAnyNight">A stay that is not active and never had a night (<see cref="TouristTaxCollectionRules.EndedBeforeAnyNight"/>).</param>
/// <param name="Collector">Who collects the tax; <c>null</c> while nobody knows (a channel stay with no rule and no declaration).</param>
/// <param name="AmountDue">What the host collects for the stay as it is now; <c>null</c> when it cannot be calculated (never an assumed 0).</param>
/// <param name="ConfirmedAmountDue">
/// The amount the host confirmed (a confirmed record that is not stale; in Switzerland the rule of the canton); <c>null</c> while there is none.
/// </param>
/// <param name="IncludedInBooking">The tax the price of the booking included (<c>Booking.TouristTaxAmount</c>), 0 when none.</param>
/// <param name="Stored">The state the record keeps (<see cref="StayTouristTax.CollectionStatus"/>): only <c>PaidAtProperty</c> is a fact of its own.</param>
/// <param name="CollectedOnline">What the guest paid online (<see cref="StayTouristTax.CollectedAmount"/>).</param>
/// <param name="RefundedOnline">What was refunded of it (<see cref="StayTouristTax.RefundedAmount"/>).</param>
public sealed record TouristTaxCollectionFacts(
    bool StayActive,
    bool EndedBeforeAnyNight,
    TouristTaxCollector? Collector,
    decimal? AmountDue,
    decimal? ConfirmedAmountDue,
    decimal IncludedInBooking,
    TouristTaxCollectionStatus Stored,
    decimal CollectedOnline,
    decimal RefundedOnline);

/// <summary>The collection of the tourist tax of a stay as it is now.</summary>
/// <param name="Status">The effective state.</param>
/// <param name="ToCollect">What is still to collect for the stay, whoever collects it (0 when nothing).</param>
/// <param name="OnlinePayable">What the guest can pay online now: <paramref name="ToCollect"/>, unless the guest already paid online once (then 0).</param>
/// <param name="RefundDue">What is owed back to the guest: paid or included over what is due, or what was paid online for a stay that never had a night.</param>
/// <param name="Reason">Why nothing can be paid online, <see cref="TouristTaxNotPayableReason.None"/> otherwise.</param>
public sealed record TouristTaxCollectionDue(
    TouristTaxCollectionStatus Status,
    decimal ToCollect,
    decimal OnlinePayable,
    decimal RefundDue,
    TouristTaxNotPayableReason Reason);

/// <summary>
/// What is due of the tourist tax of a stay and what the guest can pay online (E25/F01/US01/T01, NF-119, decision O12), pure and to the cent. The tax
/// is never charged twice:
/// <list type="bullet">
/// <item>the channel collects it: nothing to pay;</item>
/// <item>who collects is not known, or the amount cannot be calculated: nothing is charged, the host tells the guest;</item>
/// <item>the price of the booking included it: only the part of the confirmed amount above it is paid, and a lower confirmed amount is a difference to
/// give back (a «To do» of the host, never an automatic refund);</item>
/// <item>otherwise what is due less what was already paid online; one online payment per stay;</item>
/// <item>a cancelled stay or a no-show owes nothing, and gives back what was paid online when no night was spent.</item>
/// </list>
/// </summary>
public static class TouristTaxCollectionRules
{
    public static TouristTaxCollectionDue Evaluate(TouristTaxCollectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var collected = Money(Math.Max(0m, facts.CollectedOnline - facts.RefundedOnline));
        var paidOrToCollect = collected > 0m ? TouristTaxCollectionStatus.PaidOnline : TouristTaxCollectionStatus.ToCollect;

        // One online payment per stay, even when it was refunded since: what is left is collected at the property.
        var paidOnce = facts.CollectedOnline > 0m;

        if (!facts.StayActive)
        {
            if (facts.EndedBeforeAnyNight)
                return new TouristTaxCollectionDue(TouristTaxCollectionStatus.NotDue, 0m, 0m, collected, TouristTaxNotPayableReason.StayNotActive);

            var kept = facts.Stored == TouristTaxCollectionStatus.PaidAtProperty ? TouristTaxCollectionStatus.PaidAtProperty : paidOrToCollect;
            return new TouristTaxCollectionDue(kept, 0m, 0m, 0m, TouristTaxNotPayableReason.StayNotActive);
        }

        if (facts.Stored == TouristTaxCollectionStatus.PaidAtProperty)
            return new TouristTaxCollectionDue(TouristTaxCollectionStatus.PaidAtProperty, 0m, 0m, collected, TouristTaxNotPayableReason.PaidAtProperty);

        if (facts.Collector == TouristTaxCollector.Channel)
            return new TouristTaxCollectionDue(TouristTaxCollectionStatus.CollectedByChannel, 0m, 0m, collected, TouristTaxNotPayableReason.ChannelCollects);

        if (facts.Collector is null)
            return new TouristTaxCollectionDue(paidOrToCollect, 0m, 0m, 0m, TouristTaxNotPayableReason.CollectorUnknown);

        var included = Money(Math.Max(0m, facts.IncludedInBooking));
        if (included > 0m)
        {
            var settled = collected > 0m ? TouristTaxCollectionStatus.PaidOnline : TouristTaxCollectionStatus.IncludedInBookingPayment;
            if (facts.ConfirmedAmountDue is not { } confirmed)
                return new TouristTaxCollectionDue(settled, 0m, 0m, 0m, TouristTaxNotPayableReason.AwaitingConfirmation);

            return Balance(Money(confirmed) - included - collected, paidOnce, settled);
        }

        if (facts.AmountDue is not { } due)
            return new TouristTaxCollectionDue(paidOrToCollect, 0m, 0m, 0m, TouristTaxNotPayableReason.NotComputable);

        if (due <= 0m && collected == 0m)
            return new TouristTaxCollectionDue(TouristTaxCollectionStatus.NotDue, 0m, 0m, 0m, TouristTaxNotPayableReason.NothingDue);

        return Balance(Money(due) - collected, paidOnce, paidOrToCollect);
    }

    /// <summary>
    /// True for a stay that is not active and never had a night: a no-show (the guests never came), a request or a hold, or a booking cancelled with no
    /// arrival registered on or before its check-in day in Europe/Rome (<paramref name="endedAtUtc"/> when the booking does not say when it was
    /// cancelled). A stay cancelled after its check-in day, or after the arrival, may have had nights: false, the host decides.
    /// </summary>
    public static bool EndedBeforeAnyNight(Booking booking, DateTime? endedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(booking);

        if (AlloggiatiStatusRules.IsDue(booking) || booking.ArrivedAt is not null || booking.CheckedOutAt is not null)
            return false;

        if (booking.NoShowAt is not null || booking.Status == BookingStatus.Pending)
            return true;

        if (booking.Status != BookingStatus.Cancelled || (booking.CancelledAt ?? endedAtUtc) is not { } cancelledAt)
            return false;

        return RomeCalendar.DateInRome(cancelledAt) <= RomeCalendar.DateInRome(booking.CheckInDate);
    }

    public static long ToMinorUnits(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    public static decimal FromMinorUnits(long minorUnits) => minorUnits / 100m;

    private static TouristTaxCollectionDue Balance(decimal rest, bool paidOnce, TouristTaxCollectionStatus settled)
    {
        if (rest <= 0m)
            return new TouristTaxCollectionDue(settled, 0m, 0m, Money(-rest), TouristTaxNotPayableReason.NothingDue);

        return paidOnce
            ? new TouristTaxCollectionDue(TouristTaxCollectionStatus.ToCollect, rest, 0m, 0m, TouristTaxNotPayableReason.AlreadyPaidOnline)
            : new TouristTaxCollectionDue(TouristTaxCollectionStatus.ToCollect, rest, rest, 0m, TouristTaxNotPayableReason.None);
    }

    private static decimal Money(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
