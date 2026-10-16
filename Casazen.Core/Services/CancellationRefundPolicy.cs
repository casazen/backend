using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
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

/// <summary>What the guest gets back when they cancel, under the property's cancellation policy alone.</summary>
/// <param name="Percent">Share of <see cref="Booking.BasePrice"/> the policy grants, 0–100.</param>
/// <param name="Amount">
/// EUR, 2 decimals: <paramref name="Percent"/> of the base price, never more than what is still refundable, never negative.
/// </param>
public sealed record GuestRefundDecision(decimal Percent, decimal Amount);

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
    /// <summary>Long-stay (28+ nights): host overrides are ignored, the catalog policy stays (PO 2026-10-08).</summary>
    public const int LongStayNightsUnchanged = 28;

    /// <summary>
    /// Policy used for a stay: catalog, or the host's percentages / periods / refund type when the stay is shorter than
    /// <see cref="LongStayNightsUnchanged"/>.
    /// </summary>
    public static CancellationPolicy? EffectivePolicy(Property? property, int nights)
    {
        var catalog = property?.CancellationPolicy;
        if (catalog is null)
            return null;
        if (nights >= LongStayNightsUnchanged)
            return catalog;
        if (property!.CancellationRefundType == HostCancellationRefundType.NonRefundable)
        {
            return new CancellationPolicy
            {
                Name = catalog.Name,
                Description = catalog.Description,
                FullRefundHours = int.MaxValue,
                PartialRefundHours = 0,
                PartialRefundPercent = 0m,
            };
        }

        return new CancellationPolicy
        {
            Name = catalog.Name,
            Description = catalog.Description,
            FullRefundHours = property.CancellationFullRefundHours ?? catalog.FullRefundHours,
            PartialRefundHours = property.CancellationPartialRefundHours ?? catalog.PartialRefundHours,
            PartialRefundPercent = property.CancellationPartialRefundPercent ?? catalog.PartialRefundPercent,
        };
    }

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
    /// What the guest gets back when they cancel themselves (BK-02, BK-07, PO 2026-10-08): the percent of the policy that
    /// applies to the stay (<see cref="EffectivePolicy"/>: the catalog policy of the property with the host's overrides
    /// for a stay under 28 nights; no <see cref="Booking.FreeRefundDeadline"/> floor, no clause the model does not have)
    /// of <see cref="Booking.BasePrice"/> (lodging and cleaning, tourist tax excluded), capped at
    /// <paramref name="refundableAmount"/>. The one calculation of the guest's refund: the cancellation validates and
    /// pays it, and the guest-area preview of DB-08a is to show the same figure. A booking without a policy refunds
    /// nothing. Needs <see cref="Booking.Property"/> and its cancellation policy loaded.
    /// </summary>
    /// <param name="refundableAmount">What can still be refunded through Stripe (the cap); 0 when nothing was paid online.</param>
    public static GuestRefundDecision EvaluateGuestRefund(Booking booking, DateTimeOffset now, decimal refundableAmount)
    {
        ArgumentNullException.ThrowIfNull(booking);

        var nights = (booking.CheckOutDate.Date - booking.CheckInDate.Date).Days;
        return EvaluateGuestRefund(booking, EffectivePolicy(booking.Property, nights), now, refundableAmount);
    }

    /// <summary>
    /// The same calculation for a policy given by the caller (null: the booking has none). Callers of the production
    /// code use the overload that resolves the policy of the stay, so none forgets the host's overrides or counts the
    /// nights differently.
    /// </summary>
    public static GuestRefundDecision EvaluateGuestRefund(
        Booking booking,
        CancellationPolicy? policy,
        DateTimeOffset now,
        decimal refundableAmount)
    {
        ArgumentNullException.ThrowIfNull(booking);

        var percent = policy is null ? 0m : PolicyPercent(policy, booking.CheckInDate, now);
        if (refundableAmount <= 0 || percent <= 0)
            return new GuestRefundDecision(percent, 0m);

        var due = Math.Round(booking.BasePrice * percent / 100m, 2, MidpointRounding.AwayFromZero);
        return new GuestRefundDecision(percent, Math.Max(0m, Math.Min(refundableAmount, due)));
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
