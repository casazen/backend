using Casazen.Core.Entities;
using Casazen.Core.GuestPortal;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.GuestPortal;

/// <summary>
/// E50/F01/US01/T01 (NF-152, decision A2): the rules of the stay link. It opens until the end of the 30th day after the departure, a later departure
/// extends it and an earlier one never shortens it; a confirmed, checked-in or checked-out booking of any source gets one, a request, a cancelled
/// booking and a no-show do not, and neither does a stay already over by 30 days.
/// </summary>
public class GuestPortalRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>Data rule (the validity of the link): a departure on 13 October opens the link until 13 November 00:00 UTC, the end of 12 November.</summary>
    [Fact]
    public void ExpiryFor_DepartureDay_IsEndOfThirtiethDayAfter()
    {
        var departure = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);

        var expiry = GuestPortalRules.ExpiryFor(departure);

        Assert.Equal(new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc), expiry);
        Assert.Equal(DateTimeKind.Utc, expiry.Kind);
        // The 30th day after the departure is still inside, the next one is not.
        Assert.True(new DateTime(2026, 11, 12, 23, 59, 59, DateTimeKind.Utc) < expiry);
        Assert.False(new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc) < expiry);
        // The time of the departure does not move it.
        Assert.Equal(expiry, GuestPortalRules.ExpiryFor(departure.AddHours(10)));
    }

    [Fact]
    public void EffectiveExpiry_ALaterDepartureExtends_AnEarlierOneNeverShortens()
    {
        var stored = GuestPortalRules.ExpiryFor(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            GuestPortalRules.ExpiryFor(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)),
            GuestPortalRules.EffectiveExpiry(stored, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(stored, GuestPortalRules.EffectiveExpiry(stored, new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed, BookingSource.Direct)]
    [InlineData(BookingStatus.Confirmed, BookingSource.Airbnb)]
    [InlineData(BookingStatus.CheckedIn, BookingSource.BookingCom)]
    [InlineData(BookingStatus.CheckedOut, BookingSource.OtherChannel)]
    [InlineData(BookingStatus.Confirmed, BookingSource.Manual)]
    public void IneligibleReason_ActiveStayOfAnySource_IsNone(BookingStatus status, BookingSource source)
    {
        var booking = Booking(status, source, checkOut: Now.AddDays(3));

        Assert.Null(GuestPortalRules.IneligibleReason(booking, Now));
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Cancelled)]
    public void IneligibleReason_RequestOrCancelled_IsTheStatus(BookingStatus status)
    {
        Assert.Equal(GuestPortalRules.BookingStatusReason, GuestPortalRules.IneligibleReason(Booking(status, BookingSource.Direct, Now.AddDays(3)), Now));
    }

    [Fact]
    public void IneligibleReason_NoShow_IsTheStatus_AndAStayOverBy30Days_HasEnded()
    {
        var noShow = Booking(BookingStatus.Confirmed, BookingSource.Airbnb, Now.AddDays(3));
        noShow.NoShowAt = Now;
        // The departure of 8 September: its link was over at the end of 8 October.
        var ended = Booking(BookingStatus.CheckedOut, BookingSource.Direct, new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc));
        var lastDay = Booking(BookingStatus.CheckedOut, BookingSource.Direct, new DateTime(2026, 9, 9, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(GuestPortalRules.BookingStatusReason, GuestPortalRules.IneligibleReason(noShow, Now));
        Assert.Equal(GuestPortalRules.StayEndedReason, GuestPortalRules.IneligibleReason(ended, Now));
        Assert.Null(GuestPortalRules.IneligibleReason(lastDay, Now));
    }

    [Fact]
    public void ResolvableStatuses_AreTheActiveOnesAndTheCancelledOne_NeverARequest()
    {
        Assert.Equal(
            new[] { BookingStatus.Confirmed, BookingStatus.CheckedIn, BookingStatus.CheckedOut, BookingStatus.Cancelled },
            GuestPortalRules.ResolvableStatuses);
        Assert.DoesNotContain(BookingStatus.Pending, GuestPortalRules.ResolvableStatuses);
    }

    /// <summary>
    /// Privacy rule (the reserved data close with the stay): a confirmed or checked-in stay that is not a no-show may be shown the reserved data once its
    /// check-in is completed; a request, a cancelled stay, a stay already checked out and a no-show never may (the rule of the lock code).
    /// </summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed, false, true)]
    [InlineData(BookingStatus.CheckedIn, false, true)]
    [InlineData(BookingStatus.CheckedOut, false, false)]
    [InlineData(BookingStatus.Cancelled, false, false)]
    [InlineData(BookingStatus.Pending, false, false)]
    [InlineData(BookingStatus.Confirmed, true, false)]
    public void IsStayOpenToReservedData_OnlyAStayToComeOrInProgress_ThatIsNotANoShow(BookingStatus status, bool noShow, bool expected)
    {
        Assert.Equal(expected, GuestPortalRules.IsStayOpenToReservedData(status, noShow ? Now : null));
    }

    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz012345", true)]
    [InlineData("abc-_DEF0123456789abcdef", true)]
    [InlineData("", false)]
    [InlineData("short-token", false)]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz01234/", false)]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz01234=", false)]
    [InlineData("AbCdEfGhIjKlMnOp QrStUvWxYz01234", false)]
    public void IsWellFormedToken_OnlyBase64UrlOfAtLeast128Bits(string token, bool expected)
    {
        Assert.Equal(expected, GuestPortalRules.IsWellFormedToken(token));
    }

    [Fact]
    public void IsWellFormedToken_LongerThanTheColumn_IsNot()
    {
        Assert.False(GuestPortalRules.IsWellFormedToken(new string('a', GuestPortalLink.TokenMaxLength + 1)));
        Assert.False(GuestPortalRules.IsWellFormedToken(null));
    }

    /// <summary>Privacy rule: the address of the link holds the token and nothing else, on the CasaZen domain.</summary>
    [Fact]
    public void GuestPortalAddress_IsTheTokenOnTheShortPath()
    {
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.casazen.example/" }));

        Assert.Equal("https://app.casazen.example/s/AbCdEf-_123", links.GuestPortal("AbCdEf-_123"));
        Assert.Equal("/s/AbCdEf-_123", PublicSitePaths.HostGuestPortal("AbCdEf-_123"));
    }

    private static Booking Booking(BookingStatus status, BookingSource source, DateTime checkOut) => new()
    {
        Status = status,
        Source = source,
        CheckInDate = checkOut.Date.AddDays(-3),
        CheckOutDate = checkOut.Date,
    };
}
