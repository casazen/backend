using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>Host names and paths of the public site (BK-15): what is compared as a host is a plain DNS name, nothing else.</summary>
public class PublicSiteHostsTests
{
    [Theory]
    [InlineData("WWW.Example.TEST", "www.example.test")]
    [InlineData("www.example.test:443", "www.example.test")]
    [InlineData(" www.example.test. ", "www.example.test")]
    [InlineData("localhost", "localhost")]
    [InlineData("xn--caf-dma.example.test", "xn--caf-dma.example.test")]
    public void Normalize_HostName_IsLowerCaseWithoutPortOrTrailingDot(string value, string expected)
    {
        Assert.Equal(expected, PublicSiteHosts.Normalize(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("evil.test/path")]
    [InlineData("evil.test\"><script>")]
    [InlineData("https://evil.test")]
    [InlineData("user@evil.test")]
    [InlineData("evil test")]
    [InlineData("-bad.example.test")]
    [InlineData("a..b")]
    [InlineData("[::1]")]
    public void Normalize_NotAPlainDnsName_IsNull(string? value)
    {
        Assert.Null(PublicSiteHosts.Normalize(value));
    }

    [Fact]
    public void Normalize_NameLongerThanADnsName_IsNull()
    {
        Assert.Null(PublicSiteHosts.Normalize(new string('a', 254)));
        Assert.Null(PublicSiteHosts.Normalize(new string('a', 64) + ".example.test"));
    }

    [Theory]
    [InlineData("public.example.test", true)]
    [InlineData("PUBLIC.example.test:8443", true)]
    [InlineData("other.example.test", false)]
    [InlineData("sub.public.example.test", false)]
    [InlineData("public.example.test.evil.test", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPublicSiteHost_ComparesOnlyWithTheConfiguredHost(string? host, bool expected)
    {
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://public.example.test/" }));

        Assert.Equal(expected, links.IsPublicSiteHost(host));
    }

    [Fact]
    public void IsPublicSiteHost_PublicSiteNotConfigured_IsAlwaysFalse()
    {
        var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions()));

        Assert.False(links.IsPublicSiteHost("public.example.test"));
    }

    [Fact]
    public void Paths_OrgPropertyAndSitemap_AreTheRoutesOfTheWebApp()
    {
        Assert.Equal("/book/villa-rossi", PublicSitePaths.Org("villa-rossi"));
        Assert.Equal("/book/villa-rossi/property/casa-mare", PublicSitePaths.Property("villa-rossi", "casa-mare"));
        Assert.Equal("/book/villa-rossi/sitemap.xml", PublicSitePaths.OrgSitemap("villa-rossi"));
        Assert.Equal("/sitemap-book.xml", PublicSitePaths.OrgSitemapIndex);
    }

    [Fact]
    public void Paths_SlugWithSpecialCharacters_IsEscaped()
    {
        Assert.Equal("/book/a%2Fb%3Fc", PublicSitePaths.Org("a/b?c"));
    }
}
