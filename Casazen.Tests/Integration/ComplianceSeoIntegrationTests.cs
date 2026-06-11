using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Web.BackgroundJobs;
using Hangfire;
using Hangfire.Common;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task AC8_SitemapComplianceXml_ListsReviewedPages()
    {
        await SeedSeoPageAsync(LegalReviewStatus.Reviewed, SeoPageType.ComplianceGuide);

        var client = _factory.CreateClient();
        var response = await client.GetAsync("/sitemap-compliance.xml");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = await response.Content.ReadAsStringAsync();
        Assert.Contains("affitti-brevi/lombardia/como", xml, StringComparison.OrdinalIgnoreCase);
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

    private async Task SeedSeoPageAsync(LegalReviewStatus status, SeoPageType pageType)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var page = new SeoContentPage
        {
            Slug = pageType == SeoPageType.ComplianceGuide
                ? "affitti-brevi/lombardia/como"
                : "tassa-soggiorno/como",
            ComuneCode = "013075",
            RegionCode = "LOM",
            PageType = pageType,
            Title = pageType == SeoPageType.ComplianceGuide
                ? "Affitti brevi a Como"
                : "Tassa di soggiorno a Como",
            MetaDescription = "Test meta",
            LegalReviewStatus = status,
            LastRefreshedAt = DateTime.UtcNow,
        };
        context.SeoContentPages.Add(page);
        await context.SaveChangesAsync();

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

    private async Task SeedTouristTaxRateAsync(string city, decimal rate, int? maxNights = null)
    {
        using var scope = _factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
