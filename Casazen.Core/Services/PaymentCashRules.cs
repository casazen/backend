using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// When a payment counts as money collected, and how much (SR-03: the "incassato" of the Home, next to the revenue of
/// the stays, which is by accrual). One definition for the host dashboard and the payment list, translatable by EF.
/// <para>
/// By cash: what counts is the day the money came in, not the days of the stay it pays. A payment is
/// <b>collected</b> when it is <see cref="PaymentStatus.Completed"/> or <see cref="PaymentStatus.PartiallyRefunded"/> (a
/// fully refunded, failed, canceled or still open one is not), on <see cref="Payment.ProcessedAt"/> — the instant the
/// provider confirmed it — and, for a payment that has no such instant (recorded before it existed), on
/// <see cref="Payment.CreatedAt"/>, as the fiscal reports do. The day is the Europe/Rome one
/// (<see cref="RomeCalendar.StartOfDayUtc(DateTime)"/>). The amount is what is still in the host's hands: the amount
/// minus what was refunded, before any OTA withholding (the fiscal reports show the net).
/// </para>
/// </summary>
public static class PaymentCashRules
{
    /// <summary>
    /// A payment collected from <paramref name="firstDay"/> (included) to <paramref name="lastDay"/> (excluded), both Europe/Rome
    /// calendar dates (midnight UTC, the date-only convention).
    /// </summary>
    public static Expression<Func<Payment, bool>> CollectedBetween(DateTime firstDay, DateTime lastDay)
    {
        var start = RomeCalendar.StartOfDayUtc(firstDay);
        var end = RomeCalendar.StartOfDayUtc(lastDay);
        return CollectedBetweenInstants(start, end);
    }

    /// <summary>The same rule over UTC instants: from <paramref name="start"/> (included) to <paramref name="end"/> (excluded).</summary>
    public static Expression<Func<Payment, bool>> CollectedBetweenInstants(DateTime start, DateTime end) =>
        p => (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded)
             && (p.ProcessedAt ?? p.CreatedAt) >= start
             && (p.ProcessedAt ?? p.CreatedAt) < end;

    /// <summary>What a collected payment brings: its amount minus what was refunded, never below zero.</summary>
    public static decimal Collected(decimal amount, decimal refundedAmount) => Math.Max(0m, amount - refundedAmount);

    /// <summary>
    /// A payment that holds collected money, whatever the day: <see cref="PaymentStatus.Completed"/> or <see cref="PaymentStatus.PartiallyRefunded"/>,
    /// the statuses of <see cref="CollectedBetweenInstants"/> without its days (a fully refunded, failed, canceled or still open payment holds none).
    /// The one definition of the reads that add up what a booking has received (the balances to collect, NF-97), translatable by EF.
    /// </summary>
    public static Expression<Func<Payment, bool>> IsCollected() =>
        p => p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded;

    /// <summary>
    /// What a collected payment brings, as <see cref="Collected(decimal, decimal)"/> works it out, but translatable by EF so a query can add it up in
    /// SQL: the amount minus what was refunded, never below zero.
    /// </summary>
    public static Expression<Func<Payment, decimal>> CollectedAmount() =>
        p => p.Amount > p.RefundedAmount ? p.Amount - p.RefundedAmount : 0m;

    /// <summary>An amount in euros as whole cents (the amounts are stored with two decimals, so the conversion is exact).</summary>
    public static long ToCents(decimal euros) => (long)Math.Round(euros * 100m, MidpointRounding.AwayFromZero);
}
