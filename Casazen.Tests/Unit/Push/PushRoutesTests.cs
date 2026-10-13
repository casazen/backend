using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>MO-03 (A6-19): every push route is a screen of the app (<c>mobile/src/notifications/notification-routes.ts</c>).</summary>
public class PushRoutesTests
{
    [Theory]
    [InlineData("/properties", true)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/checkout", true)]
    [InlineData("/service-requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("/bookings", false)]
    [InlineData("/bookings/42", false)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/service-request", false)]
    [InlineData("https://example.com/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAppRoute_Route_MatchesOnlyScreensOfTheApp(string? route, bool expected)
    {
        Assert.Equal(expected, PushRoutes.IsAppRoute(route));
    }

    [Fact]
    public void Booking_Guid_BuildsLowerCaseAppRoutes()
    {
        var bookingId = Guid.Parse("3F2504E0-4F89-41D3-9A0C-0305E82C3301");

        Assert.Equal("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", PushRoutes.Booking(bookingId));
        Assert.Equal("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/checkout", PushRoutes.BookingCheckout(bookingId));
    }
}
