using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// US-020 (#258) programmatic compliance SEO — public pages, tourist tax calc, admin generate, sitemap.
/// </summary>
public class ComplianceSeoIntegrationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private readonly CasazenWebApplicationFactory _factory;

    public ComplianceSeoIntegrationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AC2_ComplianceGuide_Returns404_WhenNotReviewed_InTestingEnvAllowsDraft()
    {
        await SeedSeoPageAsync(LegalReviewStatus.Draft, SeoPageType.ComplianceGuide);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content/affitti-brevi/lombardia/como");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AC2_ComplianceGuide_Returns404_WhenMissing()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content/affitti-brevi/lombardia/unknown-comune");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC3_TouristTaxPage_ReturnsPage_WithRateSummary()
    {
        await SeedTouristTaxRateAsync("Como", 2.5m, maxNights: 4);
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.TouristTaxCalc);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content/tassa-soggiorno/como");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("TouristTaxCalc", doc.RootElement.GetProperty("pageType").GetString());
        Assert.True(doc.RootElement.TryGetProperty("touristTaxRate", out var rate));
        Assert.Equal(2.5m, rate.GetProperty("ratePerPersonPerNight").GetDecimal());
    }

    // "yyyy-MM-dd" dates in the body: on PostgreSQL this returned 500 before FD-06 (R-01 / A9-12).
    [Fact]
    public async Task AC12_CalculateTouristTax_UsesTouristTaxRateEntity_NotHardcoded()
    {
        await SeedTouristTaxRateAsync("Como", 2.5m, maxNights: 4);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/public/tourist-tax/calculate", new
        {
            comuneSlug = "como",
            numberOfAdults = 2,
            numberOfChildren = 0,
            checkInDate = "2026-07-01",
            checkOutDate = "2026-07-05",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(20m, doc.RootElement.GetProperty("taxAmount").GetDecimal());
        Assert.Equal(2.5m, doc.RootElement.GetProperty("ratePerPersonPerNight").GetDecimal());
    }

    [Fact]
    public async Task AC4_AdminGenerate_EnqueuesSeoPageGenerationJob()
    {
        var client = _factory.CreateAuthenticatedClient(roles: "Admin");
        var response = await client.PostAsJsonAsync("/api/admin/seo/generate", new
        {
            comuneCodes = new[] { "013075" },
            pageTypes = new[] { "ComplianceGuide", "TouristTaxCalc" },
            forceRegenerate = false,
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _factory.BackgroundJobClientMock.Verify(
            c => c.Create(
                It.Is<Job>(j => j.Type == typeof(SeoPageGenerationJob)),
                It.IsAny<Hangfire.States.IState>()),
            Times.Once);
    }

    [Fact]
    public async Task AC8_Sitemap_ListsReviewedPagesOnConfiguredPublicSite()
    {
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.ComplianceGuide);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/sitemap.xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var locations = SitemapLocations(await response.Content.ReadAsStringAsync());
        // SE-02 (A8-02): the host is App:PublicSiteBaseUrl, the domain of the web app that serves /p/* (decision D3).
        Assert.Contains($"{PublicSiteBaseUrl()}/p/affitti-brevi/lombardia/como", locations);
        Assert.Contains($"{PublicSiteBaseUrl()}/p/affitti-brevi", locations);
        Assert.All(locations, loc => Assert.StartsWith(PublicSiteBaseUrl() + "/", loc));
    }

    [Fact]
    public async Task Sitemap_OldPathOnApiHost_IsGone()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/sitemap-compliance.xml");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SitemapAndHub_PagesWithoutContentDraftOrWithoutRate_AreLeftOut()
    {
        // Reviewed guide without any revision (nothing to show), a draft with content, a calculator without a rate.
        await SeedPageAsync("013133", "affitti-brevi/lombardia/menaggio", SeoPageType.ComplianceGuide, LegalReviewStatus.Reviewed, bodyHtml: null);
        await SeedPageAsync("015146", "affitti-brevi/lombardia/milano", SeoPageType.ComplianceGuide, LegalReviewStatus.Draft, "<p>Bozza</p>");
        await SeedPageAsync("082053", "tassa-soggiorno/palermo", SeoPageType.TouristTaxCalc, LegalReviewStatus.Reviewed, "<p>Palermo</p>");

        var client = _factory.CreateClient();
        var locations = SitemapLocations(await client.GetStringAsync("/api/public/sitemap.xml"));
        using var hub = JsonDocument.Parse(await client.GetStringAsync("/api/public/content"));
        var hubPaths = hub.RootElement.GetProperty("pages").EnumerateArray()
            .Select(p => p.GetProperty("path").GetString())
            .ToList();

        foreach (var path in new[] { "/p/affitti-brevi/lombardia/menaggio", "/p/affitti-brevi/lombardia/milano", "/p/tassa-soggiorno/palermo" })
        {
            Assert.DoesNotContain(PublicSiteBaseUrl() + path, locations);
            Assert.DoesNotContain(path, hubPaths);
        }
    }

    [Fact]
    public async Task Hub_ListsTheSitemapPagesWithCanonicalOnConfiguredPublicSite()
    {
        await SeedTouristTaxRateAsync("Como", 2.5m, maxNights: 4);
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.ComplianceGuide);
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.TouristTaxCalc);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal($"{PublicSiteBaseUrl()}/p/affitti-brevi", doc.RootElement.GetProperty("canonicalUrl").GetString());
        var hubPaths = doc.RootElement.GetProperty("pages").EnumerateArray()
            .Select(p => p.GetProperty("path").GetString())
            .ToList();
        Assert.Contains("/p/affitti-brevi/lombardia/como", hubPaths);
        Assert.Contains("/p/tassa-soggiorno/como", hubPaths);

        var locations = SitemapLocations(await client.GetStringAsync("/api/public/sitemap.xml"));
        Assert.All(hubPaths, path => Assert.Contains(PublicSiteBaseUrl() + path, locations));
    }

    [Fact]
    public async Task PublicComplianceGuide_CanonicalUrl_IsOnConfiguredPublicSite()
    {
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.ComplianceGuide);

        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/public/content/affitti-brevi/lombardia/como"));

        Assert.Equal(
            $"{PublicSiteBaseUrl()}/p/affitti-brevi/lombardia/como",
            doc.RootElement.GetProperty("canonicalUrl").GetString());
    }

    [Fact]
    public async Task AC7_Disclaimers_AreReturnedOnPublicPage()
    {
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.ComplianceGuide);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content/affitti-brevi/lombardia/como");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var disclaimers = doc.RootElement.GetProperty("disclaimers");
        Assert.Contains("non consulenza legale", disclaimers.GetProperty("notLegalAdvice").GetString());
        Assert.Contains("Contenuto generato con AI", disclaimers.GetProperty("aiGenerated").GetString());
    }

    [Fact]
    public async Task PublicComplianceGuide_SeededStubBody_KeepsParagraphText()
    {
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.ComplianceGuide);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content/affitti-brevi/lombardia/como");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("<p>Guida compliance Como</p>", doc.RootElement.GetProperty("bodyHtml").GetString());
    }

    // A8-08: a revision stored before the allowlist (or by any other path) is sanitized again when served.
    [Fact]
    public async Task PublicComplianceGuide_StoredRevisionWithXssPayload_ReturnsSanitizedBody()
    {
        await SeedPageWithRawRevisionAsync(
            "013040",
            "affitti-brevi/lombardia/bellagio",
            "<h2>Bellagio</h2><img src=x onerror=alert(1)><svg/onload=alert(1)>" +
            "<p ONCLICK=\"alert(1)\">CIN <a href=\"&#106;avascript:alert(1)\">x</a> " +
            "<a href=\"https://www.example.com\">fonte</a></p><iframe src=\"https://evil.example\"></iframe>");

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/public/content/affitti-brevi/lombardia/bellagio");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "<h2>Bellagio</h2><p>CIN <a rel=\"noopener noreferrer\">x</a> " +
            "<a href=\"https://www.example.com\" rel=\"noopener noreferrer\">fonte</a></p>",
            doc.RootElement.GetProperty("bodyHtml").GetString());
    }

    [Fact]
    public async Task AddRevisionAsync_MaliciousBody_PersistsSanitizedHtml()
    {
        var pageId = await SeedPageWithRawRevisionAsync("013182", "affitti-brevi/lombardia/varenna", "<p>v1</p>");

        using (var scope = _factory.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<ISeoContentRepository>();
            await repository.AddRevisionAsync(new SeoContentRevision
            {
                PageId = pageId,
                BodyHtml = "<p onmouseover=\"alert(1)\">v2</p><script>alert(1)</script><style>p{}</style>",
                AiModelTier = AiModelTier.Economy,
                SourceDataVersion = "test-v2",
                GeneratedAt = DateTime.UtcNow.AddMinutes(1),
            });
        }

        using var verifyScope = _factory.Services.CreateScope();
        var context = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await context.SeoContentRevisions.AsNoTracking()
            .SingleAsync(r => r.PageId == pageId && r.SourceDataVersion == "test-v2");
        Assert.Equal("<p>v2</p>", stored.BodyHtml);
    }

    private string PublicSiteBaseUrl() =>
        _factory.Services.GetRequiredService<IOptions<PublicSiteOptions>>().Value.PublicSiteBaseUrl!.TrimEnd('/');

    private static List<string> SitemapLocations(string xml)
    {
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";
        return XDocument.Parse(xml).Descendants(ns + "loc").Select(loc => loc.Value).ToList();
    }

    /// <summary>A page of <paramref name="pageType"/> with an optional revision (<c>null</c>: no revision at all).</summary>
    private async Task SeedPageAsync(
        string comuneCode, string slug, SeoPageType pageType, LegalReviewStatus status, string? bodyHtml)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var page = new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comuneCode,
            RegionCode = ItalianComuneRegistry.GetByCode(comuneCode)!.RegionCode,
            PageType = pageType,
            Title = "Pagina di test",
            MetaDescription = "Test meta",
            LegalReviewStatus = status,
            LastRefreshedAt = DateTime.UtcNow,
        };
        context.SeoContentPages.Add(page);
        await context.SaveChangesAsync();

        if (bodyHtml is null)
            return;

        context.SeoContentRevisions.Add(new SeoContentRevision
        {
            PageId = page.Id,
            BodyHtml = bodyHtml,
            AiModelTier = AiModelTier.Economy,
            PromptTokens = 100,
            SourceDataVersion = "test-v1",
        });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a reviewed compliance guide page with a revision written straight to the database,
    /// bypassing the repository (simulates content stored before the allowlist).
    /// </summary>
    private async Task<Guid> SeedPageWithRawRevisionAsync(string comuneCode, string slug, string bodyHtml)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var page = new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comuneCode,
            RegionCode = "LOM",
            PageType = SeoPageType.ComplianceGuide,
            Title = "Pagina di test",
            MetaDescription = "Test meta",
            LegalReviewStatus = LegalReviewStatus.Reviewed,
            PublishedAt = DateTime.UtcNow,
            LastRefreshedAt = DateTime.UtcNow,
        };
        context.SeoContentPages.Add(page);
        await context.SaveChangesAsync();

        context.SeoContentRevisions.Add(new SeoContentRevision
        {
            PageId = page.Id,
            BodyHtml = bodyHtml,
            AiModelTier = AiModelTier.Economy,
            PromptTokens = 100,
            SourceDataVersion = "test-v1",
        });
        await context.SaveChangesAsync();
        return page.Id;
    }

    /// <summary>
    /// Ensures the Como page of <paramref name="pageType"/> exists with <paramref name="status"/>.
    /// Tests of this class share one database (class fixture) and (ComuneCode, PageType) is a unique
    /// index, so the page is updated when a previous test already created it.
    /// </summary>
    private async Task SeedSeoPageAsync(LegalReviewStatus status, SeoPageType pageType)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        const string comuneCode = "013075";
        var page = await context.SeoContentPages
            .Include(p => p.Revisions)
            .SingleOrDefaultAsync(p => p.ComuneCode == comuneCode && p.PageType == pageType);
        if (page is null)
        {
            page = new SeoContentPage
            {
                Slug = pageType == SeoPageType.ComplianceGuide
                    ? "affitti-brevi/lombardia/como"
                    : "tassa-soggiorno/como",
                ComuneCode = comuneCode,
                RegionCode = "LOM",
                PageType = pageType,
                Title = pageType == SeoPageType.ComplianceGuide
                    ? "Affitti brevi a Como"
                    : "Tassa di soggiorno a Como",
                MetaDescription = "Test meta",
            };
            context.SeoContentPages.Add(page);
        }

        page.LegalReviewStatus = status;
        page.LastRefreshedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        if (page.Revisions.Count == 0)
        {
            context.SeoContentRevisions.Add(new SeoContentRevision
            {
                PageId = page.Id,
                BodyHtml = "<article><p>Guida compliance Como</p></article>",
                AiModelTier = AiModelTier.Economy,
                PromptTokens = 100,
                SourceDataVersion = "test-v1",
            });
            await context.SaveChangesAsync();
        }
    }

    private async Task SeedTouristTaxRateAsync(string city, decimal rate, int? maxNights = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Shared class database: reuse the active rate a previous test created for this city.
        var existing = await context.TouristTaxRates.FirstOrDefaultAsync(t => t.City == city && t.IsActive);
        if (existing is not null)
        {
            existing.RatePerPersonPerNight = rate;
            existing.MaxNights = maxNights;
            await context.SaveChangesAsync();
            return;
        }

        context.TouristTaxRates.Add(new TouristTaxRate
        {
            City = city,
            RegionCode = "LOM",
            RatePerPersonPerNight = rate,
            MaxNights = maxNights,
            IsActive = true,
            EffectiveFrom = DateTime.UtcNow.AddYears(-1),
        });
        await context.SaveChangesAsync();
    }
}
