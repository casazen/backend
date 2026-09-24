using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// SE-02 (A8-02, decision D3): canonical URLs, sitemap and hub use only the configured public domain of the web app
/// (<c>App:PublicSiteBaseUrl</c>), never a domain written in code, and list only the pages worth indexing.
/// </summary>
public class SeoPublicDomainTests
{
    private const string PublicSite = "https://public-site.example.test";
    private static readonly XNamespace SitemapNs = "http://www.sitemaps.org/schemas/sitemap/0.9";

    private readonly Mock<ISeoContentRepository> _seoRepo = new();
    private readonly Mock<ITouristTaxQuoteService> _taxQuotes = new();

    [Fact]
    public async Task BuildComplianceSitemapXmlAsync_ConfiguredPublicSite_EveryUrlIsOnThatDomain()
    {
        SetupPages(Page("013075", SeoPageType.ComplianceGuide), Page("013075", SeoPageType.TouristTaxCalc));
        SetupRateInForce("013075");

        var xml = await CreateService(PublicSite + "/").BuildComplianceSitemapXmlAsync();

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", xml);
        Assert.Equal(
            [
                $"{PublicSite}/p/affitti-brevi",
                $"{PublicSite}/p/affitti-brevi/lombardia/como",
                $"{PublicSite}/p/tassa-soggiorno/como",
            ],
            Locations(xml));
        Assert.DoesNotContain("casazen", xml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BuildComplianceSitemapXmlAsync_CalculatorWithoutRateInForce_IsLeftOut()
    {
        SetupPages(Page("013075", SeoPageType.ComplianceGuide), Page("013075", SeoPageType.TouristTaxCalc));

        var xml = await CreateService(PublicSite).BuildComplianceSitemapXmlAsync();

        Assert.Equal([$"{PublicSite}/p/affitti-brevi", $"{PublicSite}/p/affitti-brevi/lombardia/como"], Locations(xml));
    }

    [Fact]
    public async Task BuildComplianceSitemapXmlAsync_UnknownComune_IsLeftOut()
    {
        SetupPages(Page("999999", SeoPageType.ComplianceGuide));

        var xml = await CreateService(PublicSite).BuildComplianceSitemapXmlAsync();

        // Nothing to index: not even the hub, which would be an empty page.
        Assert.Empty(Locations(xml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("www.example.test")]
    public async Task BuildComplianceSitemapXmlAsync_PublicSiteMissing_ThrowsInsteadOfUsingAFallbackDomain(string? publicSite)
    {
        SetupPages(Page("013075", SeoPageType.ComplianceGuide));

        await Assert.ThrowsAsync<EmailConfigurationException>(() =>
            CreateService(publicSite).BuildComplianceSitemapXmlAsync());
        _seoRepo.Verify(r => r.GetReviewedPagesForSitemapAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(SeoPageType.ComplianceGuide, "/p/affitti-brevi/lombardia/como")]
    [InlineData(SeoPageType.TouristTaxCalc, "/p/tassa-soggiorno/como")]
    public async Task PublicPage_ConfiguredPublicSite_CanonicalUrlIsOnThatDomain(SeoPageType pageType, string path)
    {
        var page = SetupPublicPage(pageType);

        var dto = await GetPublicPageAsync(CreateService(PublicSite + "/"), page.PageType);

        Assert.Equal(PublicSite + path, dto!.CanonicalUrl);
    }

    [Fact]
    public async Task PublicPage_PublicSiteMissing_HasNoCanonicalUrl()
    {
        var page = SetupPublicPage(SeoPageType.ComplianceGuide);

        var dto = await GetPublicPageAsync(CreateService(null), page.PageType);

        Assert.NotNull(dto);
        Assert.Null(dto!.CanonicalUrl);
    }

    [Theory]
    [InlineData(SeoPageType.ComplianceGuide, "compliance-guide")]
    [InlineData(SeoPageType.TouristTaxCalc, "tourist-tax-calc")]
    public async Task PublicPage_ConfiguredPublicSite_SignupCtaIsOnThatDomainWithComuneAndUtm(
        SeoPageType pageType,
        string utmContent)
    {
        // SE-03 (A8-03): the CTA opens /signup of the web app on the public domain, carrying the comune of the page.
        var page = SetupPublicPage(pageType);

        var dto = await GetPublicPageAsync(CreateService(PublicSite + "/"), page.PageType);

        Assert.Equal(
            $"{PublicSite}/signup?comune=como&utm_source=seo-compliance&utm_medium=cta&utm_content={utmContent}",
            dto!.Cta.SignupUrl);
    }

    [Fact]
    public async Task PublicPage_PublicSiteMissing_SignupCtaIsARelativePath()
    {
        var page = SetupPublicPage(SeoPageType.ComplianceGuide);

        var dto = await GetPublicPageAsync(CreateService(null), page.PageType);

        Assert.Equal(
            "/signup?comune=como&utm_source=seo-compliance&utm_medium=cta&utm_content=compliance-guide",
            dto!.Cta.SignupUrl);
    }

    [Fact]
    public async Task GetPublishedPagesAsync_ReturnsTheSitemapPagesWithTheirRoutesAndTheHubCanonical()
    {
        SetupPages(
            Page("013075", SeoPageType.ComplianceGuide, "Affitti brevi a Como"),
            Page("013075", SeoPageType.TouristTaxCalc, "Tassa di soggiorno a Como"),
            Page("013040", SeoPageType.TouristTaxCalc, "Tassa di soggiorno a Bellagio"));
        SetupRateInForce("013075");

        var result = await CreateService(PublicSite).GetPublishedPagesAsync();

        Assert.Equal($"{PublicSite}/p/affitti-brevi", result.CanonicalUrl);
        Assert.Equal(
            [
                new SeoPublishedPageDto(
                    SeoPageType.ComplianceGuide, "Affitti brevi a Como", "Como", "lombardia", "como",
                    "/p/affitti-brevi/lombardia/como"),
                new SeoPublishedPageDto(
                    SeoPageType.TouristTaxCalc, "Tassa di soggiorno a Como", "Como", "lombardia", "como",
                    "/p/tassa-soggiorno/como"),
            ],
            result.Pages);
    }

    [Fact]
    public async Task GetPublishedPagesAsync_PublicSiteMissing_ListsPagesWithoutCanonical()
    {
        SetupPages(Page("013075", SeoPageType.ComplianceGuide));

        var result = await CreateService(null).GetPublishedPagesAsync();

        Assert.Null(result.CanonicalUrl);
        Assert.Equal("/p/affitti-brevi/lombardia/como", Assert.Single(result.Pages).Path);
    }

    public SeoPublicDomainTests()
    {
        _taxQuotes
            .Setup(q => q.GetRatesInForceAsync(It.IsAny<TouristTaxComune>(), It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
    }

    private SeoContentService CreateService(string? publicSiteBaseUrl) =>
        new(
            _seoRepo.Object,
            _taxQuotes.Object,
            Mock.Of<IAiProvider>(),
            EmailTestHelpers.Links(publicSiteBaseUrl),
            Mock.Of<ILogger<SeoContentService>>(),
            new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero)));

    private static SeoContentPage Page(string comuneCode, SeoPageType pageType, string title = "Titolo") =>
        new()
        {
            Id = Guid.NewGuid(),
            ComuneCode = comuneCode,
            PageType = pageType,
            Title = title,
            LegalReviewStatus = LegalReviewStatus.Reviewed,
            LastRefreshedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        };

    private void SetupPages(params SeoContentPage[] pages) =>
        _seoRepo.Setup(r => r.GetReviewedPagesForSitemapAsync(It.IsAny<CancellationToken>())).ReturnsAsync(pages);

    private void SetupRateInForce(string istatCode) =>
        _taxQuotes
            .Setup(q => q.GetRatesInForceAsync(
                It.Is<TouristTaxComune>(c => c.IstatCode == istatCode),
                new DateOnly(2026, 9, 24),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TouristTaxRate { IstatCode = istatCode, RatePerPersonPerNight = 2m, IsActive = true }]);

    private SeoContentPage SetupPublicPage(SeoPageType pageType)
    {
        var page = Page("013075", pageType);
        _seoRepo.Setup(r => r.GetPublishedPageAsync(
                SeoPageType.ComplianceGuide, "lombardia", "como", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        _seoRepo.Setup(r => r.GetPublishedTouristTaxPageAsync("como", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        return page;
    }

    private static Task<SeoPagePublicDto?> GetPublicPageAsync(SeoContentService service, SeoPageType pageType) =>
        pageType == SeoPageType.ComplianceGuide
            ? service.GetComplianceGuideAsync("lombardia", "como", allowDraft: false)
            : service.GetTouristTaxPageAsync("como", allowDraft: false);

    private static List<string> Locations(string xml) =>
        XDocument.Parse(xml).Descendants(SitemapNs + "loc").Select(loc => loc.Value).ToList();
}
