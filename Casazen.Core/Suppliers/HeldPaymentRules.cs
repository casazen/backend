using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

public enum HeldReleaseVerdict
{
    Release,

    NotYet,

    AlreadyReleased,

    NothingToRelease,

    NotReleasable,
}

/// <summary>What the release of a payment held by CasaZen looks at (<see cref="HeldPaymentRules.Release"/>): a plain value, so every combination is a test.</summary>
/// <param name="Status">The status of the payment.</param>
/// <param name="ReleaseAt">When the money is due to the supplier; null until the work is completed.</param>
/// <param name="NextAttemptAt">The earliest time of the next try after a failed or impossible transfer; null when none is waited for.</param>
/// <param name="StripeTransferId">The transfer that paid the supplier, if there is one.</param>
/// <param name="NetCents">What the supplier receives when everything goes well: the amount minus the commission CasaZen keeps.</param>
/// <param name="RefundedCents">Refunds that succeeded.</param>
/// <param name="RefundsInProgressCents">Refunds written and not completed yet: their amount is already spoken for.</param>
/// <param name="RequestStatus">Where the request is: only a completed one is paid.</param>
/// <param name="ConfirmedByPayer">The payer said "all fine": the money goes without waiting for <paramref name="ReleaseAt"/>.</param>
public sealed record HeldReleaseFacts(
    ServicePaymentStatus Status,
    DateTime? ReleaseAt,
    DateTime? NextAttemptAt,
    string? StripeTransferId,
    int NetCents,
    int RefundedCents,
    int RefundsInProgressCents,
    ServiceRequestStatus RequestStatus,
    bool ConfirmedByPayer);

/// <summary>
/// The rules of the money of a payment <b>held by CasaZen</b> (SP-15c-a, decision VT-D1): <c>Authorized → Held → Released</c>, or a
/// refund, never both. <b>Pure functions</b>: no clock, no configuration, no database, no Stripe. Every amount is whole euro cents.
/// </summary>
/// <remarks>
/// <para><b>The invariant that keeps the money straight.</b> Of <c>amount</c> (what the payer paid), CasaZen keeps its commission
/// (<c>amount − net</c>) unless the whole payment is refunded; the supplier gets at most <c>net − refunded</c>, once; the payer gets
/// back at most <c>amount − refunded</c>. So a transfer and a refund can only coexist when together they stay within the amount, and a
/// release never happens while a refund is in progress (<see cref="Release"/>), nor a refund after the release (the status
/// <see cref="ServicePaymentStatus.Released"/> is not <see cref="IsMoneyHeld"/>).</para>
/// <para><b>Time.</b> A card block is treated as usable for the configured hours only (<see cref="CanCapture"/>); the money is due to the
/// supplier the configured minutes after the later of the completion of the work and the charge (<see cref="ReleaseAt"/>).</para>
/// </remarks>
public static class HeldPaymentRules
{
    public static bool IsHeldCycleStatus(ServicePaymentStatus status) =>
        status is ServicePaymentStatus.Authorized
            or ServicePaymentStatus.Held
            or ServicePaymentStatus.Released
            or ServicePaymentStatus.Disputed
            or ServicePaymentStatus.TransferFailed;

    public static bool IsOpen(ServicePaymentStatus status) =>
        status is ServicePaymentStatus.Authorized
            or ServicePaymentStatus.Held
            or ServicePaymentStatus.Disputed
            or ServicePaymentStatus.TransferFailed;

    public static bool IsMoneyHeld(ServicePaymentStatus status) =>
        status is ServicePaymentStatus.Held or ServicePaymentStatus.Disputed or ServicePaymentStatus.TransferFailed;

    public static DateTime AuthorizationExpiry(DateTime authorizedAt, TimeSpan validity) => authorizedAt + validity;

    /// <summary>The block has outlived its usable time. A payment with no recorded expiry never lapses by itself (Stripe's own expiry still applies).</summary>
    public static bool AuthorizationLapsed(DateTime? expiresAt, DateTime now) => expiresAt is { } limit && now >= limit;

    /// <summary>
    /// The latest instant a supplier may be left to answer a request whose card was blocked at <paramref name="authorizedAt"/>: a little before
    /// the block stops being usable (a quarter of the validity, at most an hour), so the request closes and the block is released while it is
    /// still in time, and an acceptance in the last minutes can still charge the card. A supplier's own, longer, answer time never reaches past it.
    /// </summary>
    public static DateTime LatestResponseAt(DateTime authorizedAt, TimeSpan validity)
    {
        var margin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromHours(1).Ticks, validity.Ticks / 4));
        return authorizedAt + validity - margin;
    }

    public static bool CanCapture(ServicePaymentStatus status, DateTime? authorizationExpiresAt, DateTime now) =>
        status == ServicePaymentStatus.Authorized && !AuthorizationLapsed(authorizationExpiresAt, now);

    public static DateTime ReleaseAt(DateTime completedAt, DateTime capturedAt, TimeSpan window) =>
        (completedAt > capturedAt ? completedAt : capturedAt) + window;

    /// <summary>
    /// Decides whether the money of a payment held by CasaZen is transferred to the supplier now. The order of the checks is the
    /// safety of the money: a payment that has a transfer is never released again; a refund in progress stops the release; only a
    /// completed request is paid; and the time (or the payer's "all fine") decides last.
    /// </summary>
    public static HeldReleaseVerdict Release(HeldReleaseFacts facts, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.StripeTransferId is not null || facts.Status == ServicePaymentStatus.Released)
            return HeldReleaseVerdict.AlreadyReleased;

        if (facts.Status is not (ServicePaymentStatus.Held or ServicePaymentStatus.TransferFailed))
            return HeldReleaseVerdict.NotReleasable;

        if (facts.RequestStatus != ServiceRequestStatus.Completato)
            return HeldReleaseVerdict.NotReleasable;

        if (facts.RefundsInProgressCents > 0)
            return HeldReleaseVerdict.NotYet;

        if (ReleasableCents(facts.NetCents, facts.RefundedCents, facts.RefundsInProgressCents) == 0)
            return HeldReleaseVerdict.NothingToRelease;

        if (facts.NextAttemptAt is { } next && next > now)
            return HeldReleaseVerdict.NotYet;

        if (facts.ConfirmedByPayer)
            return HeldReleaseVerdict.Release;

        return facts.ReleaseAt is { } due && due <= now ? HeldReleaseVerdict.Release : HeldReleaseVerdict.NotYet;
    }

    public static TimeSpan TransferBackoff(int failedAttempts, int baseMinutes, int maxMinutes)
    {
        if (failedAttempts <= 0)
            return TimeSpan.Zero;

        var doublings = Math.Min(failedAttempts - 1, 30);
        var minutes = Math.Min((long)baseMinutes << doublings, maxMinutes);
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    public static int ReleasableCents(int netCents, int refundedCents, int refundsInProgressCents) =>
        (int)Math.Max(0L, (long)netCents - refundedCents - refundsInProgressCents);

    public static int RefundableCents(int amountCents, int refundedCents, int refundsInProgressCents) =>
        (int)Math.Max(0L, (long)amountCents - refundedCents - refundsInProgressCents);

    public static ServicePaymentStatus StatusAfterRefunds(
        ServicePaymentStatus current,
        int amountCents,
        int netCents,
        int refundedCents,
        bool hasTransfer,
        int? supplierRefundedCents = null)
    {
        var inCycle = current is ServicePaymentStatus.Held
            or ServicePaymentStatus.TransferFailed
            or ServicePaymentStatus.Disputed
            or ServicePaymentStatus.PartiallyRefunded
            or ServicePaymentStatus.Refunded;
        if (!inCycle || hasTransfer)
            return current;

        if (refundedCents >= amountCents)
            return ServicePaymentStatus.Refunded;

        if (refundedCents > 0 && ReleasableCents(netCents, supplierRefundedCents ?? refundedCents, 0) == 0)
            return ServicePaymentStatus.PartiallyRefunded;

        return current is ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded ? ServicePaymentStatus.Held : current;
    }

    public static bool IsNearMaxHold(DateTime? capturedAt, DateTime now, int maxHoldDays, int warningDays) =>
        capturedAt is { } at && now >= at.AddDays(maxHoldDays - warningDays);
}
