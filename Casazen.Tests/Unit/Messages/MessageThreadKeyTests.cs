using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Messages;

/// <summary>
/// The three queries that read the threads of the host (the list, the badges, the hub) share one expression for which bookings have a direct thread
/// (<see cref="GuestMessageRules.IsThreadOfADirectBooking"/>); this pins it to the pure rule of the thread, <see cref="GuestMessageRules.StateOf"/>.
/// </summary>
public class DirectThreadQueryRuleTests
{
    public static TheoryData<BookingSource, BookingStatus> EverySourceAndStatus()
    {
        var data = new TheoryData<BookingSource, BookingStatus>();
        foreach (var source in Enum.GetValues<BookingSource>())
        {
            foreach (var status in Enum.GetValues<BookingStatus>())
                data.Add(source, status);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EverySourceAndStatus))]
    public void TheExpressionOfTheQueries_AgreesWithTheStateOfTheThread_ForEverySourceAndStatus(BookingSource source, BookingStatus status)
    {
        var thread = new GuestThread { Booking = new Booking { Source = source, Status = status } };
        var checkOut = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

        var hasThread = GuestMessageRules.IsThreadOfADirectBooking.Compile()(thread);

        Assert.Equal(GuestMessageRules.StateOf(source, status, checkOut, now) != GuestThreadState.Unavailable, hasThread);
    }

    [Fact]
    public void OnlyABookingOfThePublicCheckoutThatWasConfirmedAndNotCancelled_HasAThread()
    {
        var withThread = new[] { BookingStatus.Confirmed, BookingStatus.CheckedIn, BookingStatus.CheckedOut };
        var compiled = GuestMessageRules.IsThreadOfADirectBooking.Compile();

        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            Assert.Equal(withThread.Contains(status), compiled(new GuestThread { Booking = new Booking { Source = BookingSource.Direct, Status = status } }));
            Assert.False(compiled(new GuestThread { Booking = new Booking { Source = BookingSource.Manual, Status = status } }));
        }
    }
}

/// <summary>E05/F02/US01/T01: the opaque key of a conversation of the hub, and the table of what the host may do in a conversation of a channel.</summary>
public class MessageThreadKeyTests
{
    private static readonly Guid Id = Guid.Parse("3fa85f64-5717-4562-b3fc-2c963f66afa6");

    [Fact]
    public void ForOta_And_ForDirect_AreThePrefixAndTheGuidWithDashes()
    {
        Assert.Equal("ota:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadKey.ForOta(Id).ToString());
        Assert.Equal("direct:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadKey.ForDirect(Id).ToString());
    }

    [Theory]
    [InlineData("ota:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadSource.Ota)]
    [InlineData("direct:3fa85f64-5717-4562-b3fc-2c963f66afa6", MessageThreadSource.Direct)]
    [InlineData("ota:3FA85F64-5717-4562-B3FC-2C963F66AFA6", MessageThreadSource.Ota)]
    public void TryParse_ReadsAKey(string text, MessageThreadSource source)
    {
        Assert.True(MessageThreadKey.TryParse(text, out var key));
        Assert.Equal(source, key.Source);
        Assert.Equal(Id, key.Id);
        Assert.Equal(key, MessageThreadKey.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ota:")]
    [InlineData("direct:")]
    [InlineData("ota:not-a-guid")]
    [InlineData("OTA:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("other:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("ota:00000000-0000-0000-0000-000000000000")]
    [InlineData("ota:3fa85f645717456 2b3fc2c963f66afa6")]
    [InlineData("ota:3fa85f6457174562b3fc2c963f66afa6")]
    [InlineData("ota:{3fa85f64-5717-4562-b3fc-2c963f66afa6}")]
    [InlineData("ota:3fa85f64-5717-4562-b3fc-2c963f66afa6 ")]
    [InlineData("ota:3fa85f64-5717-4562-b3fc-2c963f66afa6/extra")]
    [InlineData("ota:ota:3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    public void TryParse_RefusesAnythingElse(string? text)
    {
        Assert.False(MessageThreadKey.TryParse(text, out var key));
        Assert.Equal(default, key);
        Assert.Throws<FormatException>(() => MessageThreadKey.Parse(text));
    }

    [Fact]
    public void TryParse_RefusesAHugeText_WithoutLookingAtIt()
    {
        Assert.False(MessageThreadKey.TryParse("ota:" + new string('a', 5000), out _));
    }

    [Fact]
    public void AMalformedKey_HasAStableCode()
    {
        Assert.Equal("thread_key_invalid", MessageThreadKey.InvalidCode);
    }
}

/// <summary>The one table of what the host may do in a conversation of a channel (OCT-11, D3, D4): closed by default, one place to change.</summary>
public class OtaChannelCapabilitiesTests
{
    public static TheoryData<OtaChannel, OtaThreadKind> EveryChannelAndKind()
    {
        var data = new TheoryData<OtaChannel, OtaThreadKind>();
        foreach (var channel in OtaChannelCapabilities.Channels)
        {
            foreach (var kind in OtaChannelCapabilities.Kinds)
                data.Add(channel, kind);
        }

        return data;
    }

    [Fact]
    public void TheTableCoversTheFourChannelsAndTheTwoKinds()
    {
        Assert.Equal(
            [OtaChannel.Airbnb, OtaChannel.Booking, OtaChannel.Vrbo, OtaChannel.Expedia],
            OtaChannelCapabilities.Channels);
        Assert.Equal([OtaThreadKind.Reservation, OtaThreadKind.Request], OtaChannelCapabilities.Kinds);
        Assert.Equal(Enum.GetValues<OtaChannel>().Order(), OtaChannelCapabilities.Channels.Order());
        Assert.Equal(Enum.GetValues<OtaThreadKind>().Order(), OtaChannelCapabilities.Kinds.Order());
    }

    [Theory]
    [MemberData(nameof(EveryChannelAndKind))]
    public void EveryChannel_CanReply_AndTakesNoAttachmentAndNoAutomaticAnswerUntilTheProductOwnerConfirms(OtaChannel channel, OtaThreadKind kind)
    {
        var capability = OtaChannelCapabilities.For(channel, kind);

        Assert.True(capability.CanReply);
        Assert.False(capability.AttachmentsConfirmed);
        Assert.False(capability.AutoReplyConfirmed);
    }

    [Theory]
    [MemberData(nameof(EveryChannelAndKind))]
    public void ContactDetails_AreRefusedOnlyInAnAirbnbRequest(OtaChannel channel, OtaThreadKind kind)
    {
        var airbnbRequest = channel == OtaChannel.Airbnb && kind == OtaThreadKind.Request;

        Assert.Equal(!airbnbRequest, OtaChannelCapabilities.For(channel, kind).ContactDetailsAllowed);
    }

    [Fact]
    public void AChannelOrKindThatIsNotInTheTable_CanDoNothing()
    {
        Assert.Equal(OtaChannelCapability.None, OtaChannelCapabilities.For((OtaChannel)99, OtaThreadKind.Reservation));
        Assert.Equal(OtaChannelCapability.None, OtaChannelCapabilities.For(OtaChannel.Booking, (OtaThreadKind)99));
        Assert.False(OtaChannelCapability.None.CanReply);
        Assert.False(OtaChannelCapability.None.ContactDetailsAllowed);
    }

    [Fact]
    public void ThePoliciesNotConfirmedYet_AreTheTwoSwitchesOfTheTable()
    {
        Assert.False(OtaChannelCapabilities.AttachmentsPolicyConfirmed);
        Assert.False(OtaChannelCapabilities.AutoReplyPolicyConfirmed);
    }

    [Fact]
    public void TheConversationWithTheGuestOfADirectBooking_IsAnsweredWithContactDetails_AndFollowsTheSameTwoSwitches()
    {
        var direct = OtaChannelCapabilities.Direct;

        Assert.True(direct.CanReply);
        Assert.True(direct.ContactDetailsAllowed);
        Assert.False(direct.AttachmentsConfirmed);
        Assert.False(direct.AutoReplyConfirmed);
    }
}
