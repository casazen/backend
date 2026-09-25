using Casazen.Core.Entities;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>Which rule of the model sets the minimum refund of a cancellation.</summary>
public enum CancellationRefundRule
{
    /// <summary>No rule in the model: the host chooses any amount up to what was paid.</summary>
    None,

    /// <summary>
    /// <see cref="Booking.FreeRefundDeadline"/> not passed yet: the guest was promised a free cancellation until that
    /// day (checkout page, guest booking page), so the whole amount is refunded.
    /// </summary>
    FreeCancellationDeadline,

    /// <summary>The <see cref="CancellationPolicy"/> of the property.</summary>
    PropertyCancellationPolicy,
}

/// <summary>Minimum refund (as a percentage of what was paid) that a rule of the model grants the guest.</summary>
/// <param name="Rule">The rule that applies, <see cref="CancellationRefundRule.None"/> when none does.</param>
/// <param name="Percent">Share of the paid amount due back to the guest, 0–100; null when no rule applies.</param>
public sealed record CancellationRefundFloor(CancellationRefundRule Rule, decimal? Percent)
{
    public static CancellationRefundFloor NoRule { get; } = new(CancellationRefundRule.None, null);
}

/// <summary>
/// Refund due to the guest when a booking is cancelled, from the rules already in the model (BK-02, #51). No rule is
/// invented here: with none that applies the host chooses the amount (full or partial, never more than what was paid).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Booking.FreeRefundDeadline"/>: until that calendar day (Europe/Rome, included) the cancellation is
/// free, as shown to the guest ("Cancellazione gratuita fino a …"): 100%.</item>
/// <item><see cref="CancellationPolicy"/> of the property, with the semantics of issue #51 that introduced the entity:
/// 100% when cancelled at least <see cref="CancellationPolicy.FullRefundHours"/> hours before check-in,
/// <see cref="CancellationPolicy.PartialRefundPercent"/> when at least <see cref="CancellationPolicy.PartialRefundHours"/>
/// hours before, otherwise 0%. Hours are counted to the start of the check-in day in Europe/Rome (no check-in time is
/// stored).</item>
/// <item>When both apply the guest gets the larger one: both were shown to the guest.</item>
/// </list>
/// The result is a floor: the host may always refund more, up to what was paid.
/// </remarks>
public static class CancellationRefundPolicy
{
    public static CancellationRefundFloor Evaluate(Booking booking, CancellationPolicy? policy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(booking);

        var floor = CancellationRefundFloor.NoRule;

        if (booking.FreeRefundDeadline is { } deadline && RomeCalendar.TodayAt(now) <= deadline.Date)
            floor = new CancellationRefundFloor(CancellationRefundRule.FreeCancellationDeadline, 100m);

        if (policy is not null)
        {
            var percent = PolicyPercent(policy, booking.CheckInDate, now);
            if (floor.Percent is null || percent > floor.Percent)
                floor = new CancellationRefundFloor(CancellationRefundRule.PropertyCancellationPolicy, percent);
        }

        return floor;
    }

    /// <summary>
    /// The amount (EUR, 2 decimals) still owed to the guest under <paramref name="floor"/>: its share of
    /// <paramref name="paidAmount"/> minus what is already refunded or being refunded, never below zero.
    /// </summary>
    public static decimal MinimumRefund(CancellationRefundFloor floor, decimal paidAmount, decimal alreadyRefunded)
    {
        if (floor.Percent is not { } percent || paidAmount <= 0)
            return 0m;

        var due = Math.Round(paidAmount * percent / 100m, 2, MidpointRounding.AwayFromZero);
        return Math.Max(0m, due - alreadyRefunded);
    }

    private static decimal PolicyPercent(CancellationPolicy policy, DateTime checkInDate, DateTimeOffset now)
    {
        var hoursBeforeCheckIn = (RomeCalendar.StartOfDayUtc(checkInDate) - now.UtcDateTime).TotalHours;

        if (hoursBeforeCheckIn >= policy.FullRefundHours)
            return 100m;

        if (hoursBeforeCheckIn >= policy.PartialRefundHours)
            return Math.Clamp(policy.PartialRefundPercent, 0m, 100m);

        return 0m;
    }
}
