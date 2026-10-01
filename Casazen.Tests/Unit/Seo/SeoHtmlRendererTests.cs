using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Casazen.Web.Seo;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>The head a crawler reads (BK-15): title, description, robots, canonical, hreflang, Open Graph, JSON-LD.</summary>
public class SeoHtmlRendererTests
{
    private const string Canonical = "https://public.example.test/book/villa";

    private static SeoDocument Document(bool indexable = true, string? canonical = Canonical, string? image = "https://cdn.example.test/logo.png") => new()
    {
        Language = "it",
        Title = "Villa Rossi — Prenota direttamente",
        Description = "Il tuo rifugio sul lago.",
        CanonicalUrl = canonical,
        Indexable = indexable,
        SiteName = "Villa Rossi",
        ImageUrl = image,
        JsonLd = [SeoJsonLd.Organization("Villa Rossi", canonical, image, "Il tuo rifugio sul lago.")],
        BodyHtml = "<main><h1>Villa Rossi</h1></main>\n",
    };

    [Fact]
    public void Render_IndexablePage_WritesTitleDescriptionCanonicalAndAlternates()
    {
        var html = SeoHtmlRenderer.Render(Document());

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html lang=\"it\">", html);
        Assert.Contains("<title>Villa Rossi — Prenota direttamente</title>", html);
        Assert.Contains("<meta name=\"description\" content=\"Il tuo rifugio sul lago.\">", html);
        Assert.Contains($"<link rel=\"canonical\" href=\"{Canonical}\">", html);
        Assert.Contains($"<link rel=\"alternate\" hreflang=\"it\" href=\"{Canonical}\">", html);
        Assert.Contains($"<link rel=\"alternate\" hreflang=\"x-default\" href=\"{Canonical}\">", html);
        Assert.Contains($"<meta name=\"robots\" content=\"{SeoHtmlRenderer.RobotsIndex}\">", html);
        Assert.Contains("<main><h1>Villa Rossi</h1></main>", html);
    }

    [Fact]
    public void Render_IndexablePage_WritesOpenGraphAndTwitterCard()
    {
        var html = SeoHtmlRenderer.Render(Document());

        Assert.Contains("<meta property=\"og:type\" content=\"website\">", html);
        Assert.Contains("<meta property=\"og:title\" content=\"Villa Rossi — Prenota direttamente\">", html);
        Assert.Contains("<meta property=\"og:description\" content=\"Il tuo rifugio sul lago.\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{Canonical}\">", html);
        Assert.Contains("<meta property=\"og:site_name\" content=\"Villa Rossi\">", html);
        Assert.Contains("<meta property=\"og:locale\" content=\"it_IT\">", html);
        Assert.Contains("<meta property=\"og:image\" content=\"https://cdn.example.test/logo.png\">", html);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary_large_image\">", html);
        Assert.Contains("<meta name=\"twitter:image\" content=\"https://cdn.example.test/logo.png\">", html);
    }

    [Fact]
    public void Render_NoImage_UsesTheSmallTwitterCardAndWritesNoImageTags()
    {
        var html = SeoHtmlRenderer.Render(Document(image: null));

        Assert.Contains("<meta name=\"twitter:card\" content=\"summary\">", html);
        Assert.DoesNotContain("og:image", html);
        Assert.DoesNotContain("twitter:image", html);
    }

    [Fact]
    public void Render_NotIndexablePage_IsNoindexWithoutCanonicalAlternatesOrOgUrl()
    {
        var html = SeoHtmlRenderer.Render(Document(indexable: false));

        Assert.Contains($"<meta name=\"robots\" content=\"{SeoHtmlRenderer.RobotsNoIndex}\">", html);
        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("hreflang", html);
        Assert.DoesNotContain("og:url", html);
    }

    [Fact]
    public void Render_PublicSiteNotConfigured_WritesNoCanonicalOrOgUrl()
    {
        var html = SeoHtmlRenderer.Render(Document(canonical: null));

        Assert.DoesNotContain("rel=\"canonical\"", html);
        Assert.DoesNotContain("og:url", html);
        Assert.DoesNotContain("hreflang", html);
    }

    [Fact]
    public void Render_HostileText_NeverBreaksOutOfAttributesOrElements()
    {
        var document = new SeoDocument
        {
            Language = "it",
            Title = "\"><script>alert(1)</script>",
            Description = "\" onload=\"alert(1)",
            CanonicalUrl = "https://public.example.test/book/x\"><script>alert(2)</script>",
            SiteName = "<b>x</b>",
            ImageUrl = "https://cdn.example.test/x.png\" onerror=\"alert(3)",
            JsonLd = [SeoJsonLd.Organization("</script><script>alert(4)</script>", null, null, null)],
        };

        var html = SeoHtmlRenderer.Render(document);

        // The only <script> elements are the JSON-LD blocks; nothing else is markup written by the host.
        Assert.Equal(1, Regex.Matches(html, "<script", RegexOptions.IgnoreCase).Count);
        Assert.Equal(1, Regex.Matches(html, "</script>", RegexOptions.IgnoreCase).Count);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("\" onload=\"", html);
        Assert.DoesNotContain("\" onerror=\"", html);
        Assert.DoesNotContain("<b>x</b>", html);
    }

    [Fact]
    public void Render_JsonLd_IsOneParsableBlockPerObjectWithItsContext()
    {
        var html = SeoHtmlRenderer.Render(Document());

        var blocks = Regex.Matches(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline);
        var block = Assert.Single(blocks);
        using var json = JsonDocument.Parse(block.Groups[1].Value);
        Assert.Equal("https://schema.org", json.RootElement.GetProperty("@context").GetString());
        Assert.Equal("Organization", json.RootElement.GetProperty("@type").GetString());
        Assert.Equal("Villa Rossi", json.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Render_EnglishDocument_SaysSoInLangAndLocale()
    {
        var html = SeoHtmlRenderer.Render(new SeoDocument { Language = "en", Title = "Villa", CanonicalUrl = Canonical });

        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("hreflang=\"en\"", html);
        Assert.Contains("content=\"en_US\"", html);
    }

    [Fact]
    public void Serialize_HostileJsonLdText_CannotCloseTheScriptElement()
    {
        var node = new JsonObject { ["name"] = "</script><img src=x onerror=alert(1)>" };

        var json = SeoJsonLd.Serialize(node);

        Assert.DoesNotContain("</", json);
        Assert.DoesNotContain("<", json);
        Assert.Equal("</script><img src=x onerror=alert(1)>", JsonNode.Parse(json)!["name"]!.GetValue<string>());
    }
}
