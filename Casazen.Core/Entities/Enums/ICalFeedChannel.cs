namespace Casazen.Core.Entities.Enums;

/// <summary>
/// Channel of an iCal import feed of a property (PC-11, A2-11): a host with Airbnb and Booking.com links both
/// calendars. Names match <see cref="BookingSource"/> (<c>Airbnb</c>, <c>BookingCom</c>); anything else is
/// <see cref="Other"/>, told apart by the feed label. Stored as an integer.
/// </summary>
public enum ICalFeedChannel
{
    Other = 0,
    Airbnb = 1,
    BookingCom = 2,
}
