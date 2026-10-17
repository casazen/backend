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
    /// The booking comes from the booking site or from a channel, with its price, payment and check-in link agreed there: a booking
    /// that is already paid is not edited, it is cancelled or made again (SR-D5, <see cref="BookingErrorCodes.SourceLocked"/>).
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
/// never guess it. Only a booking entered by the host (<see cref="BookingSource.Manual"/>) whose stay has not begun and that is not
/// cancelled can be changed. When more than one reason holds, the first of this order is the one given: cancelled, started, source.
/// The notes of a booking that is not cancelled can always be changed.
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

        return source == BookingSource.Manual
            ? BookingEditability.Open
            : BookingEditability.LockedBy(BookingEditLock.SourceLocked);
    }

    /// <summary>The rule for <paramref name="booking"/>.</summary>
    public static BookingEditability Of(Booking booking)
    {
        ArgumentNullException.ThrowIfNull(booking);
        return Of(booking.Status, booking.Source);
    }
}
