using Casazen.Web.Seo;
using Xunit;

namespace Casazen.Tests.Unit.Seo;

/// <summary>The links around the editorial pages: a crawler page must have a way in and out, written as plain, encoded HTML.</summary>
public class SeoPageChromeTests
{
    private static readonly SeoLink[] Nav =
    [
        new("Funzionalità", "https://public.example.test/funzionalita"),
        new("Guide", "https://public.example.test/p/affitti-brevi"),
    ];

    [Fact]
    public void Header_HasTheBrandLinkAndTheNavigation()
    {
        var html = SeoPageChrome.Header("CasaZen", "https://public.example.test/", "Navigazione principale", Nav);

        Assert.Contains("<a href=\"https://public.example.test/\">CasaZen</a>", html);
        Assert.Contains("<nav aria-label=\"Navigazione principale\">", html);
        Assert.Contains("<li><a href=\"https://public.example.test/funzionalita\">Funzionalità</a></li>", html);
        Assert.Contains("<li><a href=\"https://public.example.test/p/affitti-brevi\">Guide</a></li>", html);
        Assert.StartsWith("<header>", html);
    }

    [Fact]
    public void Breadcrumb_LinksTheStepsAndKeepsThePageItselfAsText()
    {
        var html = SeoPageChrome.Breadcrumb(
            "Percorso",
            [("CasaZen", "https://public.example.test/"), ("Guide", "https://public.example.test/p/affitti-brevi"), ("Como", "https://public.example.test/p/affitti-brevi/lombardia/como")]);

        Assert.Contains("<li><a href=\"https://public.example.test/\">CasaZen</a></li>", html);
        Assert.Contains("<li><a href=\"https://public.example.test/p/affitti-brevi\">Guide</a></li>", html);
        Assert.Contains("<li aria-current=\"page\">Como</li>", html);
        Assert.DoesNotContain("lombardia/como\"", html);
    }

    [Fact]
    public void Breadcrumb_StepWithoutAUrl_IsTextNeverALinkToNowhere()
    {
        var html = SeoPageChrome.Breadcrumb("Percorso", [("CasaZen", null), ("Como", null)]);

        Assert.DoesNotContain("<a ", html);
        Assert.Contains("<li>CasaZen</li>", html);
    }

    [Fact]
    public void Breadcrumb_EmptyTrail_IsNothing()
    {
        Assert.Equal(string.Empty, SeoPageChrome.Breadcrumb("Percorso", []));
    }

    [Fact]
    public void SeeAlso_WritesTheGroupsWithTheirHeadingsAndSkipsTheEmptyOnes()
    {
        var html = SeoPageChrome.SeeAlso(
            "Vedi anche",
            [
                new SeoLinkGroup(null, [new SeoLink("Tassa di soggiorno a Como", "https://public.example.test/p/tassa-soggiorno/como")]),
                new SeoLinkGroup("Altre guide in Lombardia", [new SeoLink("Affitti brevi a Bellagio", "https://public.example.test/p/affitti-brevi/lombardia/bellagio")]),
                new SeoLinkGroup("Vuoto", []),
            ]);

        Assert.Contains("<h2>Vedi anche</h2>", html);
        Assert.Contains("<h3>Altre guide in Lombardia</h3>", html);
        Assert.Contains("<a href=\"https://public.example.test/p/tassa-soggiorno/como\">Tassa di soggiorno a Como</a>", html);
        Assert.DoesNotContain("Vuoto", html);
    }

    [Fact]
    public void SeeAlso_NoLinkAtAll_IsNothing()
    {
        Assert.Equal(string.Empty, SeoPageChrome.SeeAlso("Vedi anche", [new SeoLinkGroup("Vuoto", [])]));
    }

    [Fact]
    public void Footer_RepeatsTheLinksAndSaysWhatCasaZenIs()
    {
        var html = SeoPageChrome.Footer("CasaZen: gestionale per affitti brevi e lunghi", "Link del sito", Nav);

        Assert.StartsWith("<footer>", html);
        Assert.Contains("<nav aria-label=\"Link del sito\">", html);
        Assert.Contains("<a href=\"https://public.example.test/funzionalita\">Funzionalità</a>", html);
        Assert.Contains("<p>CasaZen: gestionale per affitti brevi e lunghi</p>", html);
    }

    [Fact]
    public void Everything_IsEncoded()
    {
        var hostile = new SeoLink("<script>alert(1)</script>", "https://public.example.test/\"><script>alert(2)</script>");

        var html = SeoPageChrome.Header("<b>CasaZen</b>", "https://public.example.test/", "Nav\"", [hostile])
            + SeoPageChrome.SeeAlso("<i>x</i>", [new SeoLinkGroup("<u>g</u>", [hostile])])
            + SeoPageChrome.Footer("<p>", "Foot\"", [hostile]);

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<i>", html);
        Assert.DoesNotContain("<u>", html);
    }
}
