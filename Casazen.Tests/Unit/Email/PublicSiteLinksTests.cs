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
