using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// What a crawler reads on the editorial pages (guides, tourist tax pages, hub): a way in and out (header, breadcrumb, «Vedi anche»,
/// footer), titles and descriptions made from the data of the page, a summary and questions built only from the rate in force, and
/// structured data that says what the visible text says. Its own fixture: the hub lists every published page, so the pages seeded
/// here must not mix with the ones of the other SEO tests.
/// </summary>
public class PublicSeoEditorialPagesIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private const string TariffUrl = "https://comune.example.test/tariffe.pdf";
    private const string ComoIstat = "013075";
    private const string MenaggioIstat = "013145";
    private const string TaxPageSlug = "tassa-soggiorno/como";
    private static readonly DateTime Refreshed = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private readonly CasazenWebApplicationFactory _factory;

    public PublicSeoEditorialPagesIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    private string PublicSite => _factory.Services.GetRequiredService<IOptions<PublicSiteOptions>>().Value.PublicSiteBaseUrl!.TrimEnd('/');

    // ── Tourist tax page ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTouristTaxPage_OneSimpleRate_ShowsTheFactsAndQuestionsAndMarkupThatSaysTheSame()
    {
        await SeedAsync();

        var html = await GetHtmlAsync("/api/public/seo/tourist-tax/como");
        var text = WebUtility.HtmlDecode(html);

        // The title and the description are made from the data of the page.
        Assert.Contains("<title>Tassa di soggiorno a Como 2026: tariffe, esenzioni e calcolo</title>", html);
        Assert.Contains("3,00 € a persona per notte, fino a un massimo di 4 notti", Meta(html, "description"));

        // The summary: the figure, the nights, the exemption, since when and the official source.
        Assert.Contains("<h2>Imposta di soggiorno a Como in breve</h2>", text);
        Assert.Contains("<li>Tariffa: 3,00 € a persona per notte</li>", text);
        Assert.Contains("<li>Notti: fino a un massimo di 4 notti</li>", text);
        Assert.Contains("<li>Esenzioni: ospiti di età inferiore a 14 anni</li>", text);
        Assert.Contains("<li>In vigore dal: 1 gennaio 2024</li>", text);
        Assert.Contains($"<a href=\"{TariffUrl}\" rel=\"noopener noreferrer\">Tariffe pubblicate dal comune di Como</a>", html);

        // The questions, and the markup that carries the same questions and answers.
        Assert.Contains("<h2>Domande frequenti</h2>", html);
        var faq = JsonLdBlocks(html).Single(b => b.GetProperty("@type").GetString() == "FAQPage");
        var questions = faq.GetProperty("mainEntity").EnumerateArray().ToList();
        Assert.Equal(4, questions.Count);
        foreach (var question in questions)
        {
            Assert.Contains($"<h3>{question.GetProperty("name").GetString()}</h3>", text);
            Assert.Contains(question.GetProperty("acceptedAnswer").GetProperty("text").GetString()!, text);
        }

        Assert.Equal("A Como l'imposta di soggiorno è di 3,00 € a persona per notte, fino a un massimo di 4 notti.", questions[0].GetProperty("acceptedAnswer").GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetTouristTaxPage_HasAWayInAndOut_AndTheBrandForLinkPreviews()
    {
        await SeedAsync();

        var html = await GetHtmlAsync("/api/public/seo/tourist-tax/como");

        // Header and footer: the brand leads home, the pages of the site and the guides are one click away.
        Assert.Contains($"<a href=\"{PublicSite}/\">CasaZen</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/funzionalita\">Funzionalità</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/per-i-fornitori\">Per i fornitori</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/chi-siamo\">Chi siamo</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/p/affitti-brevi\">Guide</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/search\">Cerca casa</a>", html);
        Assert.Contains("<footer>\n<nav aria-label=\"Link del sito\">", html);

        // The breadcrumb is made of links, and the page itself is the last step.
        Assert.Contains("<nav aria-label=\"Percorso\">", html);
        Assert.Contains($"<li><a href=\"{PublicSite}/p/affitti-brevi\">Guide per gli affitti brevi</a></li>", html);
        Assert.Contains("<li aria-current=\"page\">Tassa di soggiorno a Como</li>", html);

        // «Vedi anche»: the guide of the same comune and the hub. No other tax page in the region: no empty group.
        Assert.Contains("<h2>Vedi anche</h2>", html);
        Assert.Contains($"<a href=\"{PublicSite}/p/affitti-brevi/lombardia/como\">Affitti brevi a Como: CIN e tassa di soggiorno</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/p/affitti-brevi\">Tutte le guide per comune</a>", html);
        Assert.DoesNotContain("Altre pagine sulla tassa di soggiorno", html);

        // Link previews: the brand image as a small card, and the platform as a site and an organization with its logo.
        Assert.Contains($"<meta property=\"og:image\" content=\"{PublicSite}/brand/icona-app-1024.png\">", html);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary\">", html);
        var blocks = JsonLdBlocks(html);
        var site = blocks.Single(b => b.GetProperty("@type").GetString() == "WebSite");
        Assert.Equal($"{PublicSite}/", site.GetProperty("url").GetString());
        var organization = blocks.Single(b => b.GetProperty("@type").GetString() == "Organization");
        Assert.Equal($"{PublicSite}/brand/icona-app-1024.png", organization.GetProperty("logo").GetString());
        var breadcrumb = blocks.Single(b => b.GetProperty("@type").GetString() == "BreadcrumbList");
        Assert.Equal(3, breadcrumb.GetProperty("itemListElement").GetArrayLength());
    }

    [Fact]
    public async Task GetTouristTaxPage_BodyWithAnEmptyListItem_NeverShowsTheEmptyBullet()
    {
        await SeedAsync();

        var html = await GetHtmlAsync("/api/public/seo/tourist-tax/como");

        Assert.Contains("<li>Fonte del comune</li>", html);
        Assert.DoesNotContain("<li></li>", html);
    }

    // ── Guide ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGuide_ShowsTheSummaryAndLinksToTheTaxPageAndTheNeighbours_ButNoQuestions()
    {
        await SeedAsync();

        var html = await GetHtmlAsync("/api/public/seo/guides/lombardia/como");
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("<title>Affitti brevi a Como: guida a CIN e tassa di soggiorno 2026</title>", html);
        Assert.Equal(
            "Guida per host a Como: CIN, Alloggiati Web e imposta di soggiorno (3,00 € a persona per notte, fino a un massimo di 4 notti). Aggiornata a ottobre 2026.",
            Meta(html, "description"));
        // The region by name, not by code.
        Assert.Contains("<p>Como · Lombardia</p>", text);
        Assert.Contains("<h2>Imposta di soggiorno a Como in breve</h2>", text);

        // The questions are the ones of the tax page: the guide has none, and no markup for them.
        Assert.DoesNotContain("Domande frequenti", text);
        Assert.DoesNotContain(JsonLdBlocks(html), b => b.GetProperty("@type").GetString() == "FAQPage");

        // «Vedi anche»: the tax page of the same comune, the hub, and the guides of the other comuni of the region.
        Assert.Contains($"<a href=\"{PublicSite}/p/tassa-soggiorno/como\">Tassa di soggiorno a Como</a>", html);
        var neighbours = Between(html, "<h3>Altre guide in Lombardia</h3>", "</ul>");
        Assert.Contains("/p/affitti-brevi/lombardia/bellagio", neighbours);
        Assert.Contains("/p/affitti-brevi/lombardia/menaggio", neighbours);
        Assert.DoesNotContain("/lombardia/como", neighbours);
        Assert.DoesNotContain("emilia-romagna", neighbours);
    }

    [Fact]
    public async Task GetGuide_ComuneWithSeveralRates_ShowsNoSummaryAndKeepsTheApprovedDescription()
    {
        await SeedAsync();

        var html = await GetHtmlAsync("/api/public/seo/guides/lombardia/menaggio");

        // The title still carries the comune and the year, but a one-line figure would be a choice among the rates: none is shown,
        // and the description stays the one the page was approved with.
        Assert.Contains("<title>Affitti brevi a Menaggio: guida a CIN e tassa di soggiorno 2026</title>", html);
        Assert.Equal("Guida approvata di Menaggio.", Meta(html, "description"));
        Assert.DoesNotContain(" in breve</h2>", html);
    }

    // ── Hub ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetHub_GroupsTheListsByRegionAndSaysWhatItLists()
    {
        await SeedAsync();

        var html = await GetHtmlAsync("/api/public/seo/hub");
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains("<title>Guide per gli affitti brevi per comune | CasaZen</title>", html);
        Assert.Contains("Guide pubblicate: 4. Pagine sulla tassa di soggiorno: 1.", text);
        Assert.True(
            html.IndexOf("<h3>Emilia-Romagna</h3>", StringComparison.Ordinal) < html.IndexOf("<h3>Lombardia</h3>", StringComparison.Ordinal),
            "the regions are in alphabetical order");
        Assert.Contains($"<a href=\"{PublicSite}/p/affitti-brevi/lombardia/como\">Affitti brevi a Como: CIN e tassa di soggiorno</a>", html);
        Assert.Contains($"<a href=\"{PublicSite}/p/tassa-soggiorno/como\">Tassa di soggiorno a Como</a>", html);

        // The description names real comuni of the list, those with both pages first.
        var description = Meta(html, "description");
        Assert.StartsWith("Guide per host di affitti brevi, comune per comune", description);
        Assert.StartsWith("Como", description[(description.IndexOf("Ad esempio: ", StringComparison.Ordinal) + "Ad esempio: ".Length)..]);

        Assert.Contains($"<a href=\"{PublicSite}/funzionalita\">Funzionalità</a>", html);
        Assert.Contains("<li aria-current=\"page\">Guide per gli affitti brevi</li>", html);
        var blocks = JsonLdBlocks(html);
        Assert.Contains(blocks, b => b.GetProperty("@type").GetString() == "WebSite");
        Assert.Contains(blocks, b => b.GetProperty("@type").GetString() == "Organization");
        Assert.Contains(blocks, b => b.GetProperty("@type").GetString() == "CollectionPage");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private async Task<string> GetHtmlAsync(string url)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static List<JsonElement> JsonLdBlocks(string html) =>
        Regex.Matches(html, "<script type=\"application/ld\\+json\">(.*?)</script>", RegexOptions.Singleline)
            .Select(m => JsonDocument.Parse(m.Groups[1].Value).RootElement.Clone())
            .ToList();

    /// <summary>The content of a <c>&lt;meta name="…"&gt;</c>, decoded.</summary>
    private static string Meta(string html, string name)
    {
        var match = Regex.Match(html, $"<meta name=\"{name}\" content=\"([^\"]*)\">");
        Assert.True(match.Success, $"meta {name} is missing");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string Between(string html, string start, string end)
    {
        var from = html.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} is missing");
        var to = html.IndexOf(end, from, StringComparison.Ordinal);
        return html[from..(to < 0 ? html.Length : to)];
    }

    /// <summary>
    /// The pages and the rates of the tests. Every test calls it: the first one seeds, the others find it done (the database is
    /// shared by the tests of the class).
    /// </summary>
    private async Task SeedAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // The tax page is the last thing seeded: when it is there, an earlier test of the class did it all.
            if (await db.SeoContentPages.AnyAsync(p => p.Slug == TaxPageSlug))
                return;

            // On PostgreSQL the migrations have already loaded the official rates of these comuni (Como with the PDF of the comune as
            // its source): the tests need figures and sources of their own, so those rows give way. The database is the class's own;
            // with the InMemory fallback there is nothing to remove.
            db.TouristTaxRates.RemoveRange(await db.TouristTaxRates
                .Where(t => t.City == "Como" || t.IstatCode == ComoIstat || t.City == "Menaggio" || t.IstatCode == MenaggioIstat)
                .ToListAsync());
            await db.SaveChangesAsync();
        }

        await SeedRateAsync("Como", ComoIstat, 3.00m, maxNights: 4, minimumAge: 14, category: null, sourceUrl: TariffUrl);
        // Menaggio has two rates by category: the pages of a comune like this show no one-line summary.
        await SeedRateAsync("Menaggio", MenaggioIstat, 2.00m, maxNights: 5, minimumAge: 12, category: "Case e appartamenti", sourceUrl: null);
        await SeedRateAsync("Menaggio", MenaggioIstat, 3.00m, maxNights: 5, minimumAge: 12, category: "Alberghi", sourceUrl: null);

        await SeedPageAsync(
            ComoIstat, "affitti-brevi/lombardia/como", "LOM", SeoPageType.ComplianceGuide,
            "Affitti brevi a Como: CIN e tassa di soggiorno", "Guida a Como.", "<h2>CIN</h2><p>Il CIN è obbligatorio.</p>");
        await SeedPageAsync(
            "013250", "affitti-brevi/lombardia/bellagio", "LOM", SeoPageType.ComplianceGuide,
            "Affitti brevi a Bellagio: CIN e tassa di soggiorno", "Guida a Bellagio.", "<h2>CIN</h2><p>Il CIN è obbligatorio.</p>");
        await SeedPageAsync(
            MenaggioIstat, "affitti-brevi/lombardia/menaggio", "LOM", SeoPageType.ComplianceGuide,
            "Affitti brevi a Menaggio: CIN e tassa di soggiorno", "Guida approvata di Menaggio.", "<h2>CIN</h2><p>Il CIN è obbligatorio.</p>");
        await SeedPageAsync(
            "037006", "affitti-brevi/emilia-romagna/bologna", "EMR", SeoPageType.ComplianceGuide,
            "Affitti brevi a Bologna: CIN e tassa di soggiorno", "Guida a Bologna.", "<h2>CIN</h2><p>Il CIN è obbligatorio.</p>");
        await SeedPageAsync(
            ComoIstat, TaxPageSlug, "LOM", SeoPageType.TouristTaxCalc,
            "Tassa di soggiorno a Como", "Calcolo a Como.",
            "<h2>Imposta di soggiorno a Como</h2><p>Testo.</p><h2>Fonti ufficiali</h2><ul><li>Fonte del comune</li><li></li></ul>");
    }

    private async Task SeedPageAsync(
        string comuneCode, string slug, string regionCode, SeoPageType pageType, string title, string metaDescription, string bodyHtml)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.SeoContentPages.AnyAsync(p => p.Slug == slug))
            return;

        var page = new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comuneCode,
            RegionCode = regionCode,
            PageType = pageType,
            Title = title,
            MetaDescription = metaDescription,
            LegalReviewStatus = LegalReviewStatus.Reviewed,
            LastRefreshedAt = Refreshed,
        };
        db.SeoContentPages.Add(page);
        await db.SaveChangesAsync();

        var revision = new SeoContentRevision
        {
            PageId = page.Id,
            BodyHtml = bodyHtml,
            AiModelTier = AiModelTier.Economy,
            SourceDataVersion = "editorial",
            GeneratedAt = Refreshed,
        };
        db.SeoContentRevisions.Add(revision);
        await db.SaveChangesAsync();
        page.PublishedRevisionId = revision.Id;
        page.PublishedAt = Refreshed;
        await db.SaveChangesAsync();
    }

    private async Task SeedRateAsync(string city, string istatCode, decimal rate, int maxNights, int minimumAge, string? category, string? sourceUrl)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.TouristTaxRates.Add(new TouristTaxRate
        {
            City = city,
            IstatCode = istatCode,
            RegionCode = "LOM",
            AccommodationCategory = category,
            RatePerPersonPerNight = rate,
            MaxNights = maxNights,
            MinimumAge = minimumAge,
            IsActive = true,
            EffectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            SourceUrl = sourceUrl,
        });
        await db.SaveChangesAsync();
    }
}
