using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// When the reviews of a booking are read from the channel manager again (E05/F06/US01/T02, OCT-12), <b>pure</b>: no clock, no database. The days are the proposal of
/// the plan, to be confirmed with the window of Octorate: every <see cref="FastInterval"/> during the first <see cref="FastDays"/> days after the check-out, then once a
/// day until day <see cref="WindowDays"/>, and no more once the guest's review is published and the host's own review is sent or closed (nothing is left to wait for).
/// A reply or a host's review whose outcome nobody confirmed keeps the booking in the schedule until the next read settles it.
/// </summary>
public static class OtaReviewPollSchedule
{
    /// <summary>Days after the check-out day the reviews of a stay are read at all.</summary>
    public const int WindowDays = 60;

    /// <summary>Days after the check-out in which the booking is read every <see cref="FastInterval"/>.</summary>
    public const int FastDays = 14;

    /// <summary>The reading of a booking in the first <see cref="FastDays"/> days.</summary>
    public static readonly TimeSpan FastInterval = TimeSpan.FromHours(6);

    /// <summary>The reading of a booking after them, until day <see cref="WindowDays"/>.</summary>
    public static readonly TimeSpan SlowInterval = TimeSpan.FromHours(24);

    /// <summary>How long a failed read waits when the cause is not going to pass by itself soon (no permission, a refusal of the request): one day.</summary>
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(24);

    /// <summary>The least time between two reads of one booking asked for by a person (<c>POST api/bookings/{id}/reviews/refresh</c>): ten minutes.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long a reply or a host's review whose outcome is not known waits to be found on the channel before it is turned into an error the host can act on: one hour.</summary>
    public static readonly TimeSpan UnconfirmedActionWindow = TimeSpan.FromHours(1);

    /// <summary>
    /// The stay is one whose reviews are read now: its check-out was before <paramref name="nowUtc"/> (nobody reviews a stay that is not over) and not more than
    /// <see cref="WindowDays"/> days ago.
    /// </summary>
    public static bool IsInWindow(DateTime checkOutUtc, DateTime nowUtc) =>
        checkOutUtc <= nowUtc && nowUtc < checkOutUtc.AddDays(WindowDays);

    /// <summary>
    /// When the booking is read next after a read that worked, or <c>null</c> when the sync is done with it.
    /// </summary>
    /// <param name="nowUtc">The instant of the read.</param>
    /// <param name="checkOutUtc">The check-out of the stay.</param>
    /// <param name="guestReviewPublished">The channel published the guest's review.</param>
    /// <param name="hostReviewWindow">Where the host's own review stands.</param>
    /// <param name="replyState">The state of the host's reply.</param>
    /// <param name="hostReviewState">The state of the send of the host's own review.</param>
    public static DateTime? NextPollAt(
        DateTime nowUtc,
        DateTime checkOutUtc,
        bool guestReviewPublished,
        HostReviewWindow hostReviewWindow,
        OtaReviewActionState replyState,
        OtaReviewActionState hostReviewState)
    {
        // An action whose outcome is not confirmed is read again at the next run, whatever the window says: it is how it ends.
        if (replyState == OtaReviewActionState.ManualVerify || hostReviewState == OtaReviewActionState.ManualVerify)
            return nowUtc;

        // The guest's review is out and the host's own is sent or closed: there is nothing left to wait for.
        if (guestReviewPublished && hostReviewWindow is HostReviewWindow.Sent or HostReviewWindow.Closed)
            return null;

        if (nowUtc >= checkOutUtc.AddDays(WindowDays))
            return null;

        return nowUtc + (nowUtc < checkOutUtc.AddDays(FastDays) ? FastInterval : SlowInterval);
    }

    /// <summary>
    /// When the booking is read next after a read that failed with <paramref name="errorCode"/>: a failure that may pass (the channel manager did not answer, its limit was
    /// reached) is read again at the next run, the others (no permission for the reviews, a refusal, a reference it does not know) after <see cref="FailureBackoff"/>, so a
    /// booking that cannot be read does not use the calls of the org every six hours. <c>null</c> once the window of the stay is over, <b>except</b> for a booking with an action whose
    /// outcome nobody confirmed (<paramref name="unconfirmedAction"/>): it is read again whatever the window says, as <see cref="NextPollAt"/> has it, because a read that failed does not
    /// end the wait and only a read ends it.
    /// </summary>
    public static DateTime? NextPollAfterFailure(DateTime nowUtc, DateTime checkOutUtc, string errorCode, bool unconfirmedAction = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        if (!unconfirmedAction && nowUtc >= checkOutUtc.AddDays(WindowDays))
            return null;

        var transient = errorCode is "octorate_unavailable" or "octorate_rate_limited";
        return nowUtc + (transient ? FastInterval : FailureBackoff);
    }
}
