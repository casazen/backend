using System.Globalization;
using System.Xml.Linq;

namespace Casazen.Web.Seo;

public static class SeoSitemaps
{
    private static readonly XNamespace Ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

    public const int MaxEntries = 50_000;

    public static string UrlSet(IEnumerable<(string Url, DateTime LastModified)> entries) =>
        Write(new XElement(Ns + "urlset", entries.Take(MaxEntries).Select(entry => Entry(Ns + "url", entry))));

    public static string Index(IEnumerable<(string Url, DateTime LastModified)> sitemaps) =>
        Write(new XElement(Ns + "sitemapindex", sitemaps.Take(MaxEntries).Select(entry => Entry(Ns + "sitemap", entry))));

    private static XElement Entry(XName name, (string Url, DateTime LastModified) entry) =>
        new(name,
            new XElement(Ns + "loc", entry.Url),
            new XElement(Ns + "lastmod", entry.LastModified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    private static string Write(XElement root)
    {
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        return document.Declaration + Environment.NewLine + document;
    }
}
