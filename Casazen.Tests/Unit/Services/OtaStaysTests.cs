using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>CO-21 (decision D7): rules of the OTA stays created from iCal blocks.</summary>
public class OtaStaysTests
{
    private static readonly DateTime Today = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(ICalFeedChannel.Airbnb, null, BookingSource.Airbnb)]
    [InlineData(ICalFeedChannel.Airbnb, BookingSource.Expedia, BookingSource.Airbnb)]
    [InlineData(ICalFeedChannel.BookingCom, null, BookingSource.BookingCom)]
    [InlineData(ICalFeedChannel.Other, BookingSource.Vrbo, BookingSource.Vrbo)]
    [InlineData(ICalFeedChannel.Other, null, null)]
    [InlineData(ICalFeedChannel.Other, BookingSource.Manual, null)]
    [InlineData(ICalFeedChannel.Other, BookingSource.Direct, null)]
    public void SourceFor_FeedChannelAndChosenSource_IsTheChannelsOtaOrNone(
        ICalFeedChannel channel,
        BookingSource? chosen,
        BookingSource? expected)
    {
        Assert.Equal(expected, OtaStays.SourceFor(channel, chosen));
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, 0, true)]
    [InlineData(BookingStatus.CheckedIn, 3, true)]
    [InlineData(BookingStatus.Confirmed, -1, false)]
    [InlineData(BookingStatus.CheckedOut, 3, false)]
    [InlineData(BookingStatus.Cancelled, 3, false)]
    public void IsReviewable_StatusAndCheckOut_OnlyActiveStaysNotOver(BookingStatus status, int checkOutInDays, bool expected)
    {
        var stay = new Booking { Status = status, CheckInDate = Today.AddDays(-2), CheckOutDate = Today.AddDays(checkOutInDays) };

        Assert.Equal(expected, OtaStays.IsReviewable(stay, Today));
    }

    [Fact]
    public void IsConvertible_ImportedBlockNotLinkedNotOver_IsTrueOtherwiseFalse()
    {
        var imported = Block(CalendarBlockSource.ICalImport, feedId: Guid.NewGuid(), from: Today, nights: 3);

        Assert.True(OtaStays.IsConvertible(imported, null, Today));
        Assert.False(OtaStays.IsConvertible(Block(CalendarBlockSource.Manual, null, Today, 3), null, Today));
        Assert.False(OtaStays.IsConvertible(Block(CalendarBlockSource.ICalImport, Guid.NewGuid(), Today.AddDays(-4), 3), null, Today));

        imported.BookingId = Guid.NewGuid();
        Assert.False(OtaStays.IsConvertible(imported, BookingStatus.Confirmed, Today));
        // A block whose stay was cancelled can become a stay again.
        Assert.True(OtaStays.IsConvertible(imported, BookingStatus.Cancelled, Today));
    }

    [Fact]
    public void IsRepresentedByStay_ActiveStayWithTheBlocksDates_IsTrueOnlyThen()
    {
        var stay = new Booking { Status = BookingStatus.Confirmed, CheckInDate = Today, CheckOutDate = Today.AddDays(3) };
        var block = Block(CalendarBlockSource.ICalImport, Guid.NewGuid(), Today, 3);
        block.BookingId = stay.Id;

        Assert.True(PropertyOccupancy.IsRepresentedByStay(block, stay));

        block.EndUtc = Today.AddDays(4);
        Assert.False(PropertyOccupancy.IsRepresentedByStay(block, stay));

        block.EndUtc = Today.AddDays(3);
        stay.Status = BookingStatus.Cancelled;
        Assert.False(PropertyOccupancy.IsRepresentedByStay(block, stay));
        Assert.False(PropertyOccupancy.IsRepresentedByStay(block, null));
    }

    private static CalendarBlock Block(CalendarBlockSource source, Guid? feedId, DateTime from, int nights) => new()
    {
        Source = source,
        FeedId = feedId,
        StartUtc = from,
        EndUtc = from.AddDays(nights),
    };
}
