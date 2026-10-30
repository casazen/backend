using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Suppliers;

/// <summary>What <see cref="HeldPaymentRules.Release"/> decides about the money of a payment held by CasaZen.</summary>
public enum HeldReleaseVerdict
{
    /// <summary>Transfer the releasable amount to the supplier now.</summary>
    Release,

    /// <summary>Not yet: the work is not done, the wait has not passed, a refund is in progress, or the next try is not due.</summary>
    NotYet,

    /// <summary>The supplier was paid already (a transfer exists): nothing to do, and above all nothing to do twice.</summary>
    AlreadyReleased,

    /// <summary>Refunds took everything the supplier would have received: nothing to transfer.</summary>
    NothingToRelease,

    /// <summary>The payment cannot be released from where it is (not charged yet, stopped by a dispute, closed, or the request was cancelled).</summary>
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
    // ─── Which states ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The statuses only a payment of the held cycle reaches.</summary>
    public static bool IsHeldCycleStatus(ServicePaymentStatus status) =>
        status is ServicePaymentStatus.Authorized
            or ServicePaymentStatus.Held
            or ServicePaymentStatus.Released
            or ServicePaymentStatus.Disputed
            or ServicePaymentStatus.TransferFailed;

    /// <summary>
    /// The money is still to be settled: blocked on the card (<see cref="ServicePaymentStatus.Authorized"/>) or held by CasaZen. It goes to
    /// the supplier or back to the payer, and until then the payment is neither paid nor free.
    /// </summary>
    public static bool IsOpen(ServicePaymentStatus status) =>
        status is ServicePaymentStatus.Authorized
            or ServicePaymentStatus.Held
            or ServicePaymentStatus.Disputed
            or ServicePaymentStatus.TransferFailed;

    /// <summary>The card was charged and CasaZen has the money: it can still be released to the supplier or refunded to the payer.</summary>
    public static bool IsMoneyHeld(ServicePaymentStatus status) =>
        status is ServicePaymentStatus.Held or ServicePaymentStatus.Disputed or ServicePaymentStatus.TransferFailed;

    // ─── The block of the card ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>When the block stops being usable: <paramref name="validity"/> after it was made. Far below what the card networks guarantee.</summary>
    public static DateTime AuthorizationExpiry(DateTime authorizedAt, TimeSpan validity) => authorizedAt + validity;

    /// <summary>The block has outlived its usable time. A payment with no recorded expiry never lapses by itself (Stripe's own expiry still applies).</summary>
    public static bool AuthorizationLapsed(DateTime? expiresAt, DateTime now) => expiresAt is { } limit && now >= limit;

    /// <summary>
    /// The card can be charged: the payment is <see cref="ServicePaymentStatus.Authorized"/> and the block has not lapsed. After it,
    /// the capture is refused and the block released: a supplier never accepts a booking with no money behind it.
    /// </summary>
    public static bool CanCapture(ServicePaymentStatus status, DateTime? authorizationExpiresAt, DateTime now) =>
        status == ServicePaymentStatus.Authorized && !AuthorizationLapsed(authorizationExpiresAt, now);

    // ─── When the supplier is paid ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the money is due to the supplier: <paramref name="window"/> after the later of the completion of the work and the charge
    /// (<c>release_at = max(completion, capture) + window</c>). A job completed before the card was charged cannot be released earlier
    /// than the window after the charge.
    /// </summary>
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

        // A cancelled or refused request is never paid: its money goes back to the payer.
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

    /// <summary>The wait before the next try of a transfer that failed <paramref name="failedAttempts"/> times: the base, doubling each time, never above the maximum.</summary>
    public static TimeSpan TransferBackoff(int failedAttempts, int baseMinutes, int maxMinutes)
    {
        if (failedAttempts <= 0)
            return TimeSpan.Zero;

        var doublings = Math.Min(failedAttempts - 1, 30);
        var minutes = Math.Min((long)baseMinutes << doublings, maxMinutes);
        return TimeSpan.FromMinutes(Math.Max(1, minutes));
    }

    // ─── Amounts ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the supplier is still owed: the net (the amount minus CasaZen's commission) minus what was given back to the payer and what is
    /// being given back. Never negative: a refund of the whole net leaves nothing to transfer, and the commission is kept.
    /// </summary>
    public static int ReleasableCents(int netCents, int refundedCents, int refundsInProgressCents) =>
        (int)Math.Max(0L, (long)netCents - refundedCents - refundsInProgressCents);

    /// <summary>What can still be given back to the payer: the amount minus what was refunded and what is being refunded. Never negative.</summary>
    public static int RefundableCents(int amountCents, int refundedCents, int refundsInProgressCents) =>
        (int)Math.Max(0L, (long)amountCents - refundedCents - refundsInProgressCents);

    // ─── After the refunds ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The status of a payment of the held cycle once the refunds that <b>succeeded</b> are known (<paramref name="refundedCents"/>):
    /// <see cref="ServicePaymentStatus.Refunded"/> when everything was given back; <see cref="ServicePaymentStatus.PartiallyRefunded"/>
    /// when nothing is left for the supplier but CasaZen kept its commission (the cycle is over); unchanged while the supplier is still
    /// owed something. A payment that has a transfer keeps its status (a refund after the release does not undo it), and so does one that
    /// is not in the cycle. A payment marked as refunded whose refund turned out not to have succeeded goes back to held.
    /// </summary>
    public static ServicePaymentStatus StatusAfterRefunds(
        ServicePaymentStatus current,
        int amountCents,
        int netCents,
        int refundedCents,
        bool hasTransfer)
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

        if (refundedCents > 0 && ReleasableCents(netCents, refundedCents, 0) == 0)
            return ServicePaymentStatus.PartiallyRefunded;

        return current is ServicePaymentStatus.PartiallyRefunded or ServicePaymentStatus.Refunded ? ServicePaymentStatus.Held : current;
    }

    // ─── How long it is held ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The money has been held for so long that it is within <paramref name="warningDays"/> days of the longest the product owner means
    /// to hold it (<paramref name="maxHoldDays"/>): the admins are warned, nothing moves (VT-D22: the limit is a decision, not a constant).
    /// </summary>
    public static bool IsNearMaxHold(DateTime? capturedAt, DateTime now, int maxHoldDays, int warningDays) =>
        capturedAt is { } at && now >= at.AddDays(maxHoldDays - warningDays);
}
