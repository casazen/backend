using System.Globalization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>The push (and bell entry) about a guest who completed the online check-in: its type, key, route and lock-screen text.</summary>
public class GuestCheckInCompletedPushTests
{
    [Fact]
    public void PushType_IsGuestCheckinCompleted()
    {
        Assert.Equal("guest-checkin-completed", PushTypes.GuestCheckInCompleted);
    }

    [Fact]
    public void DeliveryKey_OnePerCompletedSession_StableForTheSameSession()
    {
        var session = Guid.NewGuid();

        Assert.Equal($"guest-checkin:{session:N}:completed", PushDeliveryKeys.GuestCheckInCompleted(session));
        Assert.Equal(PushDeliveryKeys.GuestCheckInCompleted(session), PushDeliveryKeys.GuestCheckInCompleted(session));
        Assert.NotEqual(PushDeliveryKeys.GuestCheckInCompleted(session), PushDeliveryKeys.GuestCheckInCompleted(Guid.NewGuid()));
    }

    [Fact]
    public void DeliveryKey_FitsTheColumnOfTheBell()
    {
        Assert.InRange(
            PushDeliveryKeys.GuestCheckInCompleted(Guid.NewGuid()).Length,
            1,
            Casazen.Core.Entities.InAppNotification.DeliveryKeyMaxLength);
    }

    [Fact]
    public void Route_IsTheBookingDetail_AScreenTheAppCanOpen()
    {
        var booking = Guid.NewGuid();

        Assert.True(PushRoutes.IsAppRoute(PushRoutes.Booking(booking)));
    }

    [Theory]
    [InlineData("it", "Check-in online completato", "Villa Rosa: un ospite ha completato il check-in online.")]
    [InlineData("en", "Online check-in completed", "Villa Rosa: a guest completed the online check-in.")]
    public void GuestCheckInCompletedPush_ItalianAndEnglish_NamesThePropertyOnly(string culture, string title, string body)
    {
        var push = EmailTemplates.GuestCheckInCompletedPush(CultureInfo.GetCultureInfo(culture), "Villa Rosa");

        Assert.Equal(title, push.Title);
        Assert.Equal(body, push.Body);
    }

    [Fact]
    public void GuestCheckInCompletedPush_DefaultCulture_IsItalian()
    {
        Assert.Equal("Check-in online completato", EmailTemplates.GuestCheckInCompletedPush(EmailTemplates.DefaultCulture, "Casa").Title);
    }
}
