using System.Globalization;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>The push about a message of the guest: its type, key, route and the lock-screen text (property only).</summary>
public class GuestMessagePushTests
{
    [Fact]
    public void PushType_IsGuestMessage()
    {
        Assert.Equal("guest-message", PushTypes.GuestMessage);
    }

    [Fact]
    public void DeliveryKey_OnePerMessage_StableForTheSameMessage()
    {
        var message = Guid.NewGuid();

        Assert.Equal($"booking-message:{message:N}", PushDeliveryKeys.GuestMessage(message));
        Assert.Equal(PushDeliveryKeys.GuestMessage(message), PushDeliveryKeys.GuestMessage(message));
        Assert.NotEqual(PushDeliveryKeys.GuestMessage(message), PushDeliveryKeys.GuestMessage(Guid.NewGuid()));
    }

    [Fact]
    public void BookingMessages_BuildsARouteTheAppCanOpen()
    {
        var booking = Guid.NewGuid();

        var route = PushRoutes.BookingMessages(booking);

        Assert.Equal($"/bookings/{booking}/messages", route);
        Assert.True(PushRoutes.IsAppRoute(route));
    }

    [Theory]
    [InlineData("/bookings/not-a-guid/messages")]
    [InlineData("/bookings/00000000-0000-0000-0000-000000000000/messages/extra")]
    [InlineData("/bookings/00000000-0000-0000-0000-000000000000/messages?x=1")]
    [InlineData("/messages")]
    public void IsAppRoute_ABadMessagesRoute_IsRefused(string route)
    {
        Assert.False(PushRoutes.IsAppRoute(route));
    }

    [Theory]
    [InlineData("it", "Nuovo messaggio da un ospite", "Villa Rosa: un ospite ti ha scritto.")]
    [InlineData("en", "New message from a guest", "Villa Rosa: a guest wrote to you.")]
    public void GuestMessagePush_ItalianAndEnglish_NamesThePropertyOnly(string culture, string title, string body)
    {
        var push = EmailTemplates.GuestMessagePush(CultureInfo.GetCultureInfo(culture), "Villa Rosa");

        Assert.Equal(title, push.Title);
        Assert.Equal(body, push.Body);
    }

    [Fact]
    public void GuestMessagePush_DefaultCulture_IsItalian()
    {
        Assert.Equal("Nuovo messaggio da un ospite", EmailTemplates.GuestMessagePush(EmailTemplates.DefaultCulture, "Casa").Title);
    }
}
