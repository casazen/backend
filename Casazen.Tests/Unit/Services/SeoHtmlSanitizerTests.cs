using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A8-08 / A9-29: the SEO body is rendered with dangerouslySetInnerHTML, so only the editorial allowlist
/// may survive. Keep these payloads aligned with the frontend test src/lib/__tests__/sanitize-html.test.ts.
/// </summary>
public class SeoHtmlSanitizerTests
{
    public static TheoryData<string> XssPayloads => new()
    {
        "<img src=x onerror=alert(1)>",
        "<img src=\"x\"/onerror=\"alert(1)\">",
        "<svg/onload=alert(1)>",
        "<svg><script>alert(1)</script></svg>",
        "<script>alert(1)</script>",
        "<SCRIPT SRC=https://evil.example/x.js></SCRIPT>",
        "<iframe src=\"https://evil.example\"></iframe>",
        "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>",
        "<style>body{display:none}</style>",
        "<object data=\"javascript:alert(1)\"></object>",
        "<embed src=\"javascript:alert(1)\">",
        "<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>",
        "<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\"></noscript>",
        "<form action=\"javascript:alert(1)\"><button>x</button></form>",
        "<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\">",
        "<base href=\"javascript:alert(1)//\">",
        "<link rel=\"stylesheet\" href=\"https://evil.example/x.css\">",
    };

    [Theory]
    [MemberData(nameof(XssPayloads))]
    public void Sanitize_XssPayload_RemovesExecutableMarkup(string payload)
    {
        var result = SeoHtmlSanitizer.Sanitize(payload);

        AssertNoExecutableMarkup(result);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">clic</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">clic</a>")]
    [InlineData("<a href=\"&#x6A;avascript&#x3A;alert(1)\">clic</a>")]
    [InlineData("<a href=\"jav&#x09;ascript:alert(1)\">clic</a>")]
    [InlineData("<a href=\" JaVaScRiPt:alert(1)\">clic</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">clic</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">clic</a>")]
    [InlineData("<a href=\"/relative/path\">clic</a>")]
    [InlineData("<a href=\"//evil.example/x\">clic</a>")]
    public void Sanitize_LinkWithDisallowedHref_DropsHrefAndKeepsText(string payload)
    {
        var result = SeoHtmlSanitizer.Sanitize(payload);

        Assert.Equal("<a rel=\"noopener noreferrer\">clic</a>", result);
    }

    [Theory]
    [InlineData("<P ONCLICK=\"alert(1)\">testo</P>", "<p>testo</p>")]
    [InlineData("<p OnMouseOver=alert(1) onFocus='alert(2)'>testo</p>", "<p>testo</p>")]
    [InlineData("<h2 style=\"background:url(javascript:alert(1))\" class=\"x\" id=\"y\">titolo</h2>", "<h2>titolo</h2>")]
    [InlineData("<p data-foo=\"bar\" aria-label=\"x\">testo</p>", "<p>testo</p>")]
    public void Sanitize_DisallowedAttributes_AreRemovedCaseInsensitively(string payload, string expected)
    {
        var result = SeoHtmlSanitizer.Sanitize(payload);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Sanitize_LegitimateEditorialContent_IsPreserved()
    {
        const string html =
            "<h2>CIN obbligatorio</h2>" +
            "<p>Il <strong>CIN</strong> va esposto <em>all'ingresso</em>.<br>Vedi la fonte.</p>" +
            "<h3>Adempimenti</h3><h4>Dettagli</h4>" +
            "<ul><li>Alloggiati Web</li><li>Tassa di soggiorno</li></ul>" +
            "<ol><li>Primo</li></ol>" +
            "<table><thead><tr><th>Notti</th></tr></thead><tbody><tr><td>5</td></tr></tbody></table>";

        var result = SeoHtmlSanitizer.Sanitize(html);

        Assert.Equal(html, result);
    }

    [Theory]
    [InlineData("https://www.gazzettaufficiale.it/eli/id/2023/12/29/23G00159/sg")]
    [InlineData("http://example.com/pagina")]
    [InlineData("mailto:info@example.com")]
    public void Sanitize_LinkWithAllowedScheme_KeepsHrefAndForcesRel(string href)
    {
        var html = $"<p><a href=\"{href}\" target=\"_blank\" rel=\"opener\" title=\"t\">fonte</a></p>";

        var result = SeoHtmlSanitizer.Sanitize(html);

        Assert.Equal($"<p><a href=\"{href}\" rel=\"noopener noreferrer\">fonte</a></p>", result);
    }

    [Fact]
    public void Sanitize_StubProviderArticleWrapper_KeepsParagraphText()
    {
        // Shape produced by StubAiProvider and the bootstrap seed: the wrapper goes, the text stays.
        var result = SeoHtmlSanitizer.Sanitize(
            "<article><p>Como: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p></article>");

        Assert.Equal("<p>Como: contenuto generato per affitti brevi, CIN e tassa di soggiorno.</p>", result);
    }

    [Fact]
    public void Sanitize_DisallowedWrapperAroundScript_KeepsTextButDropsScriptContent()
    {
        var result = SeoHtmlSanitizer.Sanitize("<div><h1>Titolo</h1><script>alert(1)</script><span>testo</span></div>");

        Assert.Equal("Titolotesto", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_EmptyInput_ReturnsEmptyString(string? html)
    {
        Assert.Equal(string.Empty, SeoHtmlSanitizer.Sanitize(html));
    }

    [Fact]
    public void Sanitize_AlreadySanitizedHtml_IsStable()
    {
        const string html = "<h2>Titolo</h2><p><a href=\"https://example.com\">link</a> &amp; testo</p>";

        var once = SeoHtmlSanitizer.Sanitize(html);
        var twice = SeoHtmlSanitizer.Sanitize(once);

        Assert.Equal(once, twice);
    }

    internal static void AssertNoExecutableMarkup(string html)
    {
        foreach (var marker in new[]
                 {
                     "<script", "<svg", "<img", "<iframe", "<style", "<object", "<embed", "<math", "<form",
                     "<meta", "<base", "<link", "onerror", "onload", "javascript:", "alert(",
                 })
        {
            Assert.DoesNotContain(marker, html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
