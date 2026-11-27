using Casazen.Core.Entities;

namespace Casazen.Core.Services;

/// <summary>Why the dates and guests of a booking cannot be changed (<see cref="BookingEditRules.Of(BookingStatus, BookingSource)"/>).</summary>
public enum BookingEditLock
{
    /// <summary>The booking is cancelled (<see cref="BookingErrorCodes.NotEditable"/>).</summary>
    Cancelled,

    /// <summary>The guest has arrived or the stay is over: checked in or checked out (<see cref="BookingErrorCodes.StayLocked"/>).</summary>
    Started,

    /// <summary>
    /// The booking comes from a channel, or from the booking site and is still a request waiting (a checkout hold being paid, a "pay at the
    /// property" request the host has not accepted): its price, payment and check-in link are agreed there and are not edited
    /// (<see cref="BookingErrorCodes.SourceLocked"/>). A confirmed booking of the booking site is edited, with its money settled
    /// (E08/F02/US01/T01, NF-75), and replaces SR-D5 for it.
    /// </summary>
    SourceLocked,
}

/// <summary>Whether the dates and guests of a booking may be changed, and why not.</summary>
/// <param name="Allowed">True when the host may change the dates and the guests.</param>
/// <param name="Reason">Why not; <c>null</c> when <paramref name="Allowed"/>.</param>
public readonly record struct BookingEditability(bool Allowed, BookingEditLock? Reason)
{
    /// <summary>The dates and guests may be changed.</summary>
    public static BookingEditability Open { get; } = new(true, null);

    /// <summary>The dates and guests cannot be changed, for <paramref name="reason"/>.</summary>
    public static BookingEditability LockedBy(BookingEditLock reason) => new(false, reason);
}

/// <summary>
/// Whether the host may change the dates and the guests of a booking (SR-05a): the one rule of <c>PUT /api/bookings/{id}</c>
/// (<c>HostBookingService</c>) and of the <c>canEdit</c> every booking answer carries, so the booking sheet and the calendar
/// never guess it. Only a booking entered by the host (<see cref="BookingSource.Manual"/>) or a confirmed one of the booking site
/// (<see cref="BookingSource.Direct"/>, E08/F02/US01/T01) whose stay has not begun and that is not cancelled can be changed. When more than
/// one reason holds, the first of this order is the one given: cancelled, started, source. The notes of a booking that is not cancelled can
/// always be changed.
/// </summary>
public static class BookingEditRules
{
    /// <summary>The rule for a booking of <paramref name="status"/> and <paramref name="source"/>.</summary>
    public static BookingEditability Of(BookingStatus status, BookingSource source)
    {
        if (status == BookingStatus.Cancelled)
            return BookingEditability.LockedBy(BookingEditLock.Cancelled);

        if (status is BookingStatus.CheckedIn or BookingStatus.CheckedOut)
            return BookingEditability.LockedBy(BookingEditLock.Started);

        // Entered by the host: open. From the booking site: open once confirmed, with the price worked out again and the money settled on Stripe
        // (E08/F02/US01/T01, NF-75); a hold still being paid or a "pay at the property" request waiting for the host is not a booking yet. The
        // sources of a channel stay locked here: their rules are the channel's (E08/F02/US02/T01).
        var open = source == BookingSource.Manual || (source == BookingSource.Direct && status == BookingStatus.Confirmed);
        return open
            ? BookingEditability.Open
            : BookingEditability.LockedBy(BookingEditLock.SourceLocked);
    }

    /// <summary>The rule for <paramref name="booking"/>.</summary>
    public static BookingEditability Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking.Status, booking.Source);
    }

    /// <summary>
    /// Whether a booking in <paramref name="status"/> can still be cancelled: waiting, confirmed or in the house. The one definition of the
    /// cancellation of the host and of the guest (<c>BookingCancellationService</c>) and of the change options of a booking.
    /// </summary>
    public static bool IsCancellable(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.CheckedIn;

    /// <summary>
    /// What the host may do with <paramref name="booking"/> (E08/F02/US02/T01, NF-75, NF-76): to change it (<see cref="BookingChangeOptions.CanEdit"/>)
    /// and to cancel it (<see cref="BookingChangeOptions.CanCancel"/>), and, when not, why and how to ask for it. A booking entered by the host and
    /// a direct one follow the rules above and the status; a booking that came from a channel through the channel manager (a channel source and an
    /// Octorate reservation) follows <paramref name="policy"/> as well, which is closed for every channel unless the configuration opens it. Without
    /// a policy every channel is closed.
    /// </summary>
    /// <remarks>
    /// <see cref="BookingChangeOptions.ReasonCode"/> and <see cref="BookingChangeOptions.GuidanceKey"/> explain the first thing that is not allowed
    /// (the change, else the cancellation); the cancellation has its own pair, so a screen that cancels shows the right sentence even when the change
    /// is not allowed either. A change of a booking that is not the host's is locked where its price and payment came from
    /// (<see cref="BookingEditLock.SourceLocked"/>): an open channel does not change that, <c>CanEdit</c> is what <c>PUT /api/bookings/{id}</c> accepts.
    /// </remarks>
    public static BookingChangeOptions ChangeOptionsOf(Booking booking, OtaChannelActionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(booking);

        var editability = Of(booking);
        var rule = OtaChannelActionPolicy.IsChannelBooking(booking) ? (policy ?? OtaChannelActionPolicy.Closed).For(booking.Source) : null;

        // The change: the status and the source decide it; a channel booking that is locked by its source says how to ask on the channel.
        string? editReason = null;
        string? editGuidance = null;
        if (!editability.Allowed)
        {
            editReason = editability.Reason switch
            {
                BookingEditLock.Cancelled => BookingErrorCodes.NotEditable,
                BookingEditLock.Started => BookingErrorCodes.StayLocked,
                _ => BookingErrorCodes.SourceLocked,
            };

            if (rule is not null && editability.Reason == BookingEditLock.SourceLocked)
            {
                editReason = rule.CanModify ? BookingErrorCodes.SourceLocked : BookingErrorCodes.OtaBookingChangeNotAllowed;
                editGuidance = rule.ModifyGuidanceKey;
            }
        }

        // The cancellation: the status decides it, then the channel.
        var canCancel = IsCancellable(booking.Status);
        string? cancelReason = null;
        string? cancelGuidance = null;
        if (!canCancel)
        {
            cancelReason = booking.Status == BookingStatus.Cancelled ? BookingErrorCodes.AlreadyCancelled : BookingErrorCodes.NotCancellable;
        }
        else if (rule is { CanCancel: false })
        {
            canCancel = false;
            cancelReason = BookingErrorCodes.OtaBookingCancelNotAllowed;
            cancelGuidance = rule.CancelGuidanceKey;
        }

        var changeBlocked = !editability.Allowed;
        return new BookingChangeOptions(
            editability.Allowed,
            canCancel,
            changeBlocked ? editReason : cancelReason,
            changeBlocked ? editGuidance : cancelGuidance,
            rule?.Channel,
            cancelReason,
            cancelGuidance);
    }
}

/// <summary>
/// What the host may do with a booking (<see cref="BookingEditRules.ChangeOptionsOf"/>, E08/F02/US02/T01): the answer every booking carries so the
/// screens never guess it.
/// </summary>
/// <param name="CanEdit">The host may change the dates and the guests (the rule of <c>PUT /api/bookings/{id}</c>).</param>
/// <param name="CanCancel">The host may cancel the booking from CasaZen.</param>
/// <param name="ReasonCode">Why not, for the first thing that is not allowed (the change, else the cancellation): a code of <see cref="BookingErrorCodes"/>; <c>null</c> when both are allowed.</param>
/// <param name="GuidanceKey">The key of the one sentence that says how to ask the guest or the channel for that; <c>null</c> when the booking is not one of a channel or there is nothing to ask.</param>
/// <param name="Channel">The channel of a booking that came from one (<c>BookingCom</c>); <c>null</c> otherwise.</param>
/// <param name="CancelReasonCode">Why the cancellation is not allowed, whatever the change; <c>null</c> when it is.</param>
/// <param name="CancelGuidanceKey">The key of the sentence about the cancellation on the channel, when the channel is why it is not allowed.</param>
public sealed record BookingChangeOptions(
    bool CanEdit,
    bool CanCancel,
    string? ReasonCode,
    string? GuidanceKey,
    string? Channel,
    string? CancelReasonCode = null,
    string? CancelGuidanceKey = null);
