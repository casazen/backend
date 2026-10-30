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
/// EUR, 2 decimals: <paramref name="Percent"/> of the base price plus the tourist tax (DB-D15 (1a)), never more than what is still
/// refundable, never negative.
/// </param>
public sealed record GuestRefundDecision(decimal Percent, decimal Amount);

/// <summary>
/// The days a cancellation of a stay still gets the full refund and the partial one (<see cref="CancellationRefundPolicy.RefundWindows"/>),
/// Europe/Rome calendar dates, <b>included</b>: the last whole day. A window the policy does not have is <c>null</c>.
/// </summary>
/// <param name="FullRefundUntil">Last day with the full refund, <c>null</c> when the policy never refunds in full.</param>
/// <param name="PartialRefundPercent">The share of the partial window, <c>null</c> when the policy has none.</param>
/// <param name="PartialRefundUntil">Last day with the partial refund, <c>null</c> when the policy has none.</param>
public sealed record GuestRefundWindows(DateOnly? FullRefundUntil, decimal? PartialRefundPercent, DateOnly? PartialRefundUntil)
{
    /// <summary>No policy: no window.</summary>
    public static GuestRefundWindows None { get; } = new(null, null, null);
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
    /// <summary>Long-stay (28+ nights): host overrides are ignored, the catalog policy stays (PO 2026-10-08).</summary>
    public const int LongStayNightsUnchanged = 28;

    /// <summary>
    /// A full-refund window of this many hours (ten years) or more is how a policy says "never refunded in full" (the catalog
    /// row "Non rimborsabile" and a stay the host marked non-refundable, DB-D2): no booking is made so far ahead, so the window never
    /// opens, and the date it would end on is still a date (an <c>int.MaxValue</c> of hours is not one).
    /// </summary>
    public const int NeverFullRefundHours = 24 * 365 * 10;

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
                FullRefundHours = NeverFullRefundHours,
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

    /// <summary>
    /// The policy of the stay of <paramref name="booking"/>: <see cref="EffectivePolicy(Property?, int)"/> for the nights it has. The
    /// one place that counts the nights, so the refund, the preview and the windows of a booking read the same policy. Needs
    /// <see cref="Booking.Property"/> and its cancellation policy loaded; <c>null</c> when it has none.
    /// </summary>
    public static CancellationPolicy? EffectivePolicy(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return EffectivePolicy(booking.Property, (booking.CheckOutDate.Date - booking.CheckInDate.Date).Days);
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
    /// What the guest gets back when they cancel themselves (BK-02, BK-07, PO 2026-10-08, DB-08a): the percent of the policy that
    /// applies to the stay (<see cref="EffectivePolicy"/>: the catalog policy of the property with the host's overrides
    /// for a stay under 28 nights; no <see cref="Booking.FreeRefundDeadline"/> floor, no clause the model does not have)
    /// of <see cref="Booking.BasePrice"/> (lodging and cleaning), <b>plus the whole tourist tax</b> (default of DB-D15 (1a): the tax is a
    /// debt to the municipality, not due on a stay that does not happen, so it goes back whatever the policy says; to be confirmed
    /// with the consultant, and then the change is here), capped at <paramref name="refundableAmount"/>. The one calculation of the
    /// guest's refund: the cancellation validates and pays it, and the guest-area preview shows the same figure. A booking without a
    /// policy refunds only the tax. Needs <see cref="Booking.Property"/> and its cancellation policy loaded.
    /// </summary>
    /// <param name="refundableAmount">What can still be refunded through Stripe (the cap); 0 when nothing was paid online.</param>
    public static GuestRefundDecision EvaluateGuestRefund(Booking booking, DateTimeOffset now, decimal refundableAmount)
    {
        ArgumentNullException.ThrowIfNull(booking);

        return EvaluateGuestRefund(booking, EffectivePolicy(booking), now, refundableAmount);
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
        if (refundableAmount <= 0)
            return new GuestRefundDecision(percent, 0m);

        // The policy's share of the price of the stay, and the tourist tax in full (DB-D15 (1a)).
        var stay = percent <= 0 ? 0m : Math.Round(booking.BasePrice * percent / 100m, 2, MidpointRounding.AwayFromZero);
        var due = stay + Math.Max(0m, booking.TouristTax);
        return new GuestRefundDecision(percent, Math.Max(0m, Math.Min(refundableAmount, due)));
    }

    /// <summary>
    /// The last calendar days (Europe/Rome, included) on which a cancellation still gets the full refund and the partial one, for
    /// the guest-area preview and the "cancellation until" graph of DB-08a/b: the same reading of <paramref name="policy"/> as
    /// <see cref="PolicyPercent"/> and as the free refund deadline of the checkout
    /// (<see cref="DirectBookingPaymentRules.FreeRefundDeadline"/>): the last <i>whole</i> day that is still at least that many
    /// hours before the start of the check-in day, so a date shown is never a promise an hour too long. A window the policy does not
    /// have (no policy, "never refunded in full", no partial share) is <c>null</c>.
    /// </summary>
    public static GuestRefundWindows RefundWindows(CancellationPolicy? policy, DateTime checkInDate)
    {
        if (policy is null)
            return GuestRefundWindows.None;

        var startOfCheckInDay = RomeCalendar.StartOfDayUtc(checkInDate);
        DateOnly? full = policy.FullRefundHours < NeverFullRefundHours
            ? LastWholeDayAtLeast(startOfCheckInDay, policy.FullRefundHours)
            : null;

        var partialPercent = Math.Clamp(policy.PartialRefundPercent, 0m, 100m);
        if (partialPercent <= 0)
            return new GuestRefundWindows(full, null, null);

        // A partial window that ends before the full one starts to count is not a window of its own (a policy written upside down).
        var partial = LastWholeDayAtLeast(startOfCheckInDay, policy.PartialRefundHours);
        return full is { } fullUntil && partial >= fullUntil
            ? new GuestRefundWindows(full, null, null)
            : new GuestRefundWindows(full, partialPercent, partial);
    }

    /// <summary>
    /// True when <paramref name="effective"/> is the catalog policy itself, so the catalog description tells what the guest gets; false
    /// when the host's overrides changed its windows or percentages (the description of the catalog would then say something else).
    /// </summary>
    public static bool IsCatalogPolicy(CancellationPolicy? catalog, CancellationPolicy? effective) =>
        catalog is not null
        && effective is not null
        && catalog.FullRefundHours == effective.FullRefundHours
        && catalog.PartialRefundHours == effective.PartialRefundHours
        && catalog.PartialRefundPercent == effective.PartialRefundPercent;

    /// <summary>
    /// The last whole Europe/Rome day that ends at least <paramref name="hours"/> before <paramref name="startOfCheckInDayUtc"/>:
    /// the day before the one that contains the instant <paramref name="hours"/> before it (daylight saving included).
    /// </summary>
    internal static DateOnly LastWholeDayAtLeast(DateTime startOfCheckInDayUtc, int hours) =>
        RomeCalendar.DateInRome(startOfCheckInDayUtc.AddHours(-Math.Clamp(hours, 0, NeverFullRefundHours))).AddDays(-1);

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

        // "Never refunded in full" (DB-D2): the window of a non-refundable policy does not open, whatever the hours.
        if (policy.FullRefundHours < NeverFullRefundHours && hoursBeforeCheckIn >= policy.FullRefundHours)
            return 100m;

        if (hoursBeforeCheckIn >= policy.PartialRefundHours)
            return Math.Clamp(policy.PartialRefundPercent, 0m, 100m);

        return 0m;
    }
}
