using System.Xml.Linq;
using Casazen.Web.Seo;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>XML of the booking-site sitemaps (BK-15), in the format of the compliance sitemap (SE-02).</summary>
public class SeoSitemapsTests
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    [Fact]
    public void UrlSet_Entries_AreUrlsWithLocAndDateOnlyLastmod()
    {
        var xml = SeoSitemaps.UrlSet([("https://public.example.test/book/a", new DateTime(2026, 9, 30, 23, 59, 0, DateTimeKind.Utc))]);

        var document = XDocument.Parse(xml);
        Assert.Equal("urlset", document.Root!.Name.LocalName);
        var url = Assert.Single(document.Root.Elements(Ns + "url"));
        Assert.Equal("https://public.example.test/book/a", url.Element(Ns + "loc")!.Value);
        Assert.Equal("2026-09-30", url.Element(Ns + "lastmod")!.Value);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
    }

    [Fact]
    public void Index_Entries_AreSitemaps()
    {
        var xml = SeoSitemaps.Index([("https://public.example.test/book/a/sitemap.xml", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc))]);

        var document = XDocument.Parse(xml);
        Assert.Equal("sitemapindex", document.Root!.Name.LocalName);
        Assert.Single(document.Root.Elements(Ns + "sitemap"));
    }

    [Fact]
    public void UrlSet_UrlWithXmlCharacters_IsEscaped()
    {
        var xml = SeoSitemaps.UrlSet([("https://public.example.test/book/a?x=1&y=<2>", DateTime.UtcNow)]);

        Assert.Equal("https://public.example.test/book/a?x=1&y=<2>", XDocument.Parse(xml).Descendants(Ns + "loc").Single().Value);
        Assert.DoesNotContain("<2>", xml);
    }

    [Fact]
    public void UrlSet_NoEntries_IsAnEmptyUrlSet()
    {
        Assert.Empty(XDocument.Parse(SeoSitemaps.UrlSet([])).Root!.Elements());
    }
}
