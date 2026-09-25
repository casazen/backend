using Casazen.Infrastructure.Email;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// PL-11 (A1-31, decision D3): the Stripe Checkout and billing portal return pages are built from
/// <c>App:PublicSiteBaseUrl</c> on a real route of the web app, and a return URL sent by the client is accepted only on
/// that same site (no open redirect after the payment). PL-16: only on the allow-listed plan and billing pages of each
/// rental context.
/// </summary>
public class BillingReturnUrlTests
{
    private const string PublicSite = "https://public-site.example.test";

    [Fact]
    public void BillingCheckoutSuccess_ConfiguredDomain_BuildsPlanPageOnThatDomain()
    {
        var links = EmailTestHelpers.Links(PublicSite + "/");

        Assert.Equal($"{PublicSite}/app/short-rent/settings/plan?checkout=success", links.BillingCheckoutSuccess());
    }

    [Fact]
    public void BillingCheckoutCancel_ConfiguredDomain_BuildsPlanPageOnThatDomain()
    {
        var links = EmailTestHelpers.Links(PublicSite);

        Assert.Equal($"{PublicSite}/app/short-rent/settings/plan?checkout=cancel", links.BillingCheckoutCancel());
    }

    [Fact]
    public void BillingPortalReturn_ConfiguredDomain_BuildsPlanPageOnThatDomain()
    {
        var links = EmailTestHelpers.Links("https://other-domain.example.test");

        Assert.Equal("https://other-domain.example.test/app/short-rent/settings/plan", links.BillingPortalReturn());
    }

    [Theory]
    [InlineData("https://public-site.example.test/app/short-rent/settings/plan?checkout=success")]
    [InlineData("https://PUBLIC-SITE.example.test/app/short-rent/settings/plan?session_id={CHECKOUT_SESSION_ID}")]
    [InlineData("https://public-site.example.test:443/app/billing")]
    [InlineData("  https://public-site.example.test/  ")]
    public void IsOnPublicSite_PageOfThePublicSite_ReturnsTrue(string url)
    {
        Assert.True(EmailTestHelpers.Links(PublicSite).IsOnPublicSite(url));
    }

    [Theory]
    [InlineData("https://evil.example/phishing")]
    [InlineData("https://public-site.example.test.evil.example/app")]
    [InlineData("https://evil-public-site.example.test/app")]
    [InlineData("https://example.test/app")]
    [InlineData("http://public-site.example.test/app")]
    [InlineData("https://public-site.example.test:8443/app")]
    [InlineData("https://user@public-site.example.test/app")]
    [InlineData("//evil.example/app")]
    [InlineData("/app/short-rent/settings/plan")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://public-site.example.test/app")]
    public void IsOnPublicSite_OtherHostSchemePortOrNotAbsolute_ReturnsFalse(string url)
    {
        Assert.False(EmailTestHelpers.Links(PublicSite).IsOnPublicSite(url));
    }

    [Theory]
    [InlineData("/app/long-rent/settings/plan")]
    [InlineData("/app/long-rent/settings/billing")]
    [InlineData("/app/short-rent/settings/billing")]
    public void BillingCheckoutSuccess_AllowListedReturnPath_BuildsThatPageOnThePublicSite(string returnPath)
    {
        // PL-16 (A1-36): the landlord comes back to the plan/billing page of the context it started from.
        var links = EmailTestHelpers.Links(PublicSite);

        Assert.Equal($"{PublicSite}{returnPath}?checkout=success", links.BillingCheckoutSuccess(returnPath));
        Assert.Equal($"{PublicSite}{returnPath}?checkout=cancel", links.BillingCheckoutCancel(returnPath));
        Assert.Equal($"{PublicSite}{returnPath}", links.BillingPortalReturn(returnPath));
    }

    [Theory]
    [InlineData("/app/long-rent/leases")]
    [InlineData("https://evil.example/app/long-rent/settings/plan")]
    [InlineData("//evil.example/app/long-rent/settings/plan")]
    [InlineData("/app/long-rent/settings/plan?next=https://evil.example")]
    [InlineData("/APP/LONG-RENT/SETTINGS/PLAN")]
    public void BillingCheckoutSuccess_ReturnPathOutsideAllowList_Throws(string returnPath)
    {
        var links = EmailTestHelpers.Links(PublicSite);

        Assert.False(PublicSiteLinks.IsBillingReturnPagePath(returnPath));
        Assert.Throws<ArgumentException>(() => links.BillingCheckoutSuccess(returnPath));
    }

    [Theory]
    [InlineData("https://public-site.example.test/app/long-rent/settings/plan?checkout=success")]
    [InlineData("https://public-site.example.test/app/short-rent/settings/plan?checkout=success&session_id={CHECKOUT_SESSION_ID}")]
    [InlineData("https://public-site.example.test/app/long-rent/settings/billing")]
    public void IsBillingReturnUrl_AllowListedPageOfThePublicSite_ReturnsTrue(string url)
    {
        Assert.True(EmailTestHelpers.Links(PublicSite).IsBillingReturnUrl(url));
    }

    [Theory]
    [InlineData("https://public-site.example.test/app/billing")]
    [InlineData("https://public-site.example.test/app/long-rent/leases?checkout=success")]
    [InlineData("https://public-site.example.test/")]
    [InlineData("https://evil.example/app/long-rent/settings/plan")]
    [InlineData("/app/long-rent/settings/plan")]
    public void IsBillingReturnUrl_OtherPageOrSite_ReturnsFalse(string url)
    {
        Assert.False(EmailTestHelpers.Links(PublicSite).IsBillingReturnUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsOnPublicSite_PublicSiteNotConfigured_ReturnsFalse(string? baseUrl)
    {
        var links = EmailTestHelpers.Links(baseUrl);

        Assert.False(links.IsConfigured);
        Assert.False(links.IsOnPublicSite("https://public-site.example.test/app"));
    }
}
