using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// OTA stays created by the host from an iCal block (CO-21, decision D7, GC-AC9). An iCal feed publishes dates only:
/// the host gives the guest's name and email and the block becomes a confirmed <see cref="Booking"/> with the OTA source
/// of its feed, so the check-in link (CO-09), Alloggiati Web (CO-11, CO-12), the cockpit (CO-04, CO-10) and the arrival
/// and check-out (CO-08) work as for any other stay. The block stays linked (<see cref="CalendarBlock.BookingId"/>, and
/// <see cref="Booking.ICalFeedId"/> + <see cref="Booking.ExternalId"/> on the stay).
/// <para>
/// Rules: only a block imported from a feed, not converted yet (or whose stay was cancelled), whose stay is not over
/// (check-out today or later, Europe/Rome) and that overlaps no other booking. No price is made up: the amount is the
/// host's, when given, otherwise zero. A later sync never changes nor cancels the stay: a block gone from the feed, or
/// with other dates, marks it "da verificare" (<see cref="OtaStayReviewReason"/>) and alerts the host once
/// (docs/runbooks/ical.md, "OTA stays from iCal blocks").
/// </para>
/// </summary>
public static class OtaStays
{
    public const int DefaultNumberOfGuests = 1;

    /// <summary>
    /// Source of a stay created from a block of a feed of <paramref name="channel"/>: the OTA of the channel, or
    /// <paramref name="chosen"/> for a feed of another channel (it must be an OTA source, see
    /// <see cref="FiscalCopy.IsOtaBookingSource"/>). Null when no OTA source can be told.
    /// </summary>
    public static BookingSource? SourceFor(ICalFeedChannel channel, BookingSource? chosen) => channel switch
    {
        ICalFeedChannel.Airbnb => BookingSource.Airbnb,
        ICalFeedChannel.BookingCom => BookingSource.BookingCom,
        _ => chosen is { } source && FiscalCopy.IsOtaBookingSource(source) ? source : null,
    };

    public static bool IsReviewable(Booking stay, DateTime todayInRome) =>
        stay.Status is BookingStatus.Confirmed or BookingStatus.CheckedIn
        && stay.CheckOutDate.Date >= todayInRome.Date;

    public static bool IsConvertible(CalendarBlock block, BookingStatus? linkedStayStatus, DateTime todayInRome) =>
        block.Source == CalendarBlockSource.ICalImport
        && block.FeedId is not null
        && (block.BookingId is null || linkedStayStatus is null or BookingStatus.Cancelled)
        && block.EndUtc.Date >= todayInRome.Date
        && block.EndUtc.Date > block.StartUtc.Date;
}

public static class OtaStayErrorCodes
{
    public const string BlockNotFound = "ical_block_not_found";

    public const string BlockNotImported = "ota_stay_block_not_imported";

    public const string AlreadyConverted = "ota_stay_block_already_converted";

    public const string OverlapsBooking = "ota_stay_block_overlaps_booking";

    public const string BlockEnded = "ota_stay_block_ended";

    public const string SourceRequired = "ota_stay_source_required";

    public const string ChannelDatesUnavailable = "ota_stay_channel_dates_unavailable";

    public const string BlockNotFoundMessageKey = "OtaStayBlockNotFound";
    public const string BlockNotImportedMessageKey = "OtaStayBlockNotImported";
    public const string AlreadyConvertedMessageKey = "OtaStayBlockAlreadyConverted";
    public const string OverlapsBookingMessageKey = "OtaStayBlockOverlapsBooking";
    public const string BlockEndedMessageKey = "OtaStayBlockEnded";
    public const string SourceRequiredMessageKey = "OtaStaySourceRequired";
    public const string ChannelDatesUnavailableMessageKey = "OtaStayChannelDatesUnavailable";
}
