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
    public static Expression<Func<Payment, bool>> CollectedBetween(DateTime firstDay, DateTime lastDay)
    {
        var start = RomeCalendar.StartOfDayUtc(firstDay);
        var end = RomeCalendar.StartOfDayUtc(lastDay);
        return CollectedBetweenInstants(start, end);
    }

    public static Expression<Func<Payment, bool>> CollectedBetweenInstants(DateTime start, DateTime end) =>
        p => (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded)
             && (p.ProcessedAt ?? p.CreatedAt) >= start
             && (p.ProcessedAt ?? p.CreatedAt) < end;

    public static decimal Collected(decimal amount, decimal refundedAmount) => Math.Max(0m, amount - refundedAmount);

    public static Expression<Func<Payment, bool>> IsCollected() =>
        p => p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded;

    public static Expression<Func<Payment, decimal>> CollectedAmount() =>
        p => p.Amount > p.RefundedAmount ? p.Amount - p.RefundedAmount : 0m;

    public static long ToCents(decimal euros) => (long)Math.Round(euros * 100m, MidpointRounding.AwayFromZero);
}
