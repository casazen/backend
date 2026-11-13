using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>MO-03 (A6-19): every push route is a screen of the app (<c>mobile/src/notifications/notification-routes.ts</c>).</summary>
public class PushRoutesTests
{
    [Theory]
    [InlineData("/properties", true)]
    [InlineData("/approvals", true)]
    [InlineData("/approvals/", false)]
    [InlineData("/approvals/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("/approvals?x=1", false)]
    [InlineData("https://example.com/approvals", false)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/checkout", true)]
    [InlineData("/supplier/requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("/messages/ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("/messages/direct:3f2504e0-4f89-41d3-9a0c-0305e82c3301", true)]
    [InlineData("/messages", false)]
    [InlineData("/messages/", false)]
    [InlineData("/messages/42", false)]
    [InlineData("/messages/other:3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("/messages/ota:not-a-guid", false)]
    [InlineData("/messages/ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301/reply", false)]
    [InlineData("/messages/ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301?x=1", false)]
    [InlineData("https://example.com/messages/ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("/service-requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("/bookings", false)]
    [InlineData("/bookings/42", false)]
    [InlineData("/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301/service-request", false)]
    [InlineData("/supplier/requests/42", false)]
    [InlineData("/supplier/requests", false)]
    [InlineData("/supplier/requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301/photos", false)]
    [InlineData("/supplier/requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301/../../login", false)]
    [InlineData("https://example.com/bookings/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
    [InlineData("https://example.com/supplier/requests/3f2504e0-4f89-41d3-9a0c-0305e82c3301", false)]
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

    [Theory]
    [InlineData("ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("direct:3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    public void MessageThread_ThreadKey_BuildsTheRouteOfTheConversation(string threadKey)
    {
        var route = PushRoutes.MessageThread(threadKey);

        Assert.Equal($"/messages/{threadKey}", route);
        Assert.True(PushRoutes.IsAppRoute(route));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ota:")]
    [InlineData("ota:42")]
    [InlineData("other:3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("3f2504e0-4f89-41d3-9a0c-0305e82c3301")]
    [InlineData("ota:3f2504e0-4f89-41d3-9a0c-0305e82c3301/../../login")]
    [InlineData("ota:3F2504E0-4F89-41D3-9A0C-0305E82C3301")]
    public void MessageThread_AKeyThatIsNotAConversation_IsRefused(string threadKey)
    {
        Assert.Throws<ArgumentException>(() => PushRoutes.MessageThread(threadKey));
    }

    [Fact]
    public void Approvals_BuildsTheRouteOfTheListOfTheItemsWaitingForADecision()
    {
        var route = PushRoutes.Approvals();

        Assert.Equal("/approvals", route);
        Assert.True(PushRoutes.IsAppRoute(route));
    }

    [Fact]
    public void SupplierRequest_Guid_BuildsTheLowerCaseRouteOfTheSupplierArea()
    {
        var requestId = Guid.Parse("9B1DEB4D-3B7D-4BAD-9BDD-2B0D7B3DCB6D");

        var route = PushRoutes.SupplierRequest(requestId);

        Assert.Equal("/supplier/requests/9b1deb4d-3b7d-4bad-9bdd-2b0d7b3dcb6d", route);
        Assert.True(PushRoutes.IsAppRoute(route));
    }
}
