using Casazen.Infrastructure.Email;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>FD-13 (A4-07, D3): email links always come from App:PublicSiteBaseUrl, never from a default domain.</summary>
public class PublicSiteLinksTests
{
    [Fact]
    public void SupplierInbox_ConfiguredBaseUrlWithTrailingSlash_BuildsLinkOnThatDomain()
    {
        var links = EmailTestHelpers.Links("https://staging.example.org/");

        Assert.Equal("https://staging.example.org/app/supplier/inbox", links.SupplierInbox());
    }

    [Fact]
    public void GuestCheckIn_Token_IsEscapedInPath()
    {
        var links = EmailTestHelpers.Links("https://app.example.org");

        Assert.Equal("https://app.example.org/checkin/a%2Fb%3Fc", links.GuestCheckIn("a/b?c"));
    }

    [Fact]
    public void SupplierInviteSignup_Token_PointsToWebAppRegisterPageWithTokenOnly()
    {
        var links = EmailTestHelpers.Links("https://app.example.org");
        var token = new string('a', 64);

        var url = links.SupplierInviteSignup(token);

        // SU-01 (A4-03): the web app page, never the backend; no email or comune in the URL.
        Assert.Equal($"https://app.example.org/register?inviteToken={token}", url);
    }

    [Fact]
    public void OnSiteRequestConfirmation_SlugAndToken_PointToBookingSitePageWithIdAndTokenOnly()
    {
        var links = EmailTestHelpers.Links("https://app.example.org");
        var bookingId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        var url = links.OnSiteRequestConfirmation("villa rosa", bookingId, "t0k_en-1");

        // BK-06: the page of the org's booking site; no personal data in the URL.
        Assert.Equal(
            "https://app.example.org/book/villa%20rosa/requests/0f8fad5b-d9cb-469f-a165-70867728950e/confirm?token=t0k_en-1",
            url);
    }

    [Fact]
    public void HostBookingRequests_PointsToConsoleRequestsView()
    {
        var links = EmailTestHelpers.Links("https://app.example.org/");

        Assert.Equal("https://app.example.org/app/short-rent/bookings?view=requests", links.HostBookingRequests());
    }

    [Fact]
    public void GuestBookings_OrgSlug_PointsToMyBookingsOfTheBookingSite()
    {
        var links = EmailTestHelpers.Links("https://app.example.org/");

        // BK-10: no booking id, email or checkout token in the URL; the page asks for the code and the email.
        Assert.Equal("https://app.example.org/book/villa%20rosa/my-bookings", links.GuestBookings("villa rosa"));
    }

    [Fact]
    public void HostBooking_BookingId_PointsToConsoleBookingDetail()
    {
        var links = EmailTestHelpers.Links("https://app.example.org");
        var bookingId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

        Assert.Equal(
            "https://app.example.org/app/short-rent/bookings/0f8fad5b-d9cb-469f-a165-70867728950e",
            links.HostBooking(bookingId));
    }

    [Fact]
    public void GuestBookings_PublicSiteBaseUrlMissing_ThrowsConfigurationError()
    {
        var links = EmailTestHelpers.Links(null);

        Assert.Throws<EmailConfigurationException>(() => links.GuestBookings("villa"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("casazen-app.vercel.app")]
    [InlineData("/relative")]
    [InlineData("ftp://files.example.org")]
    [InlineData("https://app.example.org/?utm=1")]
    public void Build_MissingOrInvalidBaseUrl_ThrowsConfigurationErrorInsteadOfFallback(string? baseUrl)
    {
        var links = EmailTestHelpers.Links(baseUrl);

        Assert.False(links.IsConfigured);
        Assert.Throws<EmailConfigurationException>(() => links.SupplierInbox());
        Assert.Throws<EmailConfigurationException>(() => links.GuestCheckIn("token"));
        Assert.Throws<EmailConfigurationException>(links.EnsureConfigured);
    }
}
