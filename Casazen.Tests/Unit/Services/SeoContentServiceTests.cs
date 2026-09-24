using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SeoContentServiceTests
{
    private static readonly TimeProvider Today = new FixedTimeProvider(new DateTimeOffset(2026, 9, 24, 10, 0, 0, TimeSpan.Zero));

    /// <summary>The real quote service (the only tourist tax engine) over a repository holding <paramref name="rates"/>.</summary>
    private static TouristTaxQuoteService QuoteService(params TouristTaxRate[] rates)
    {
        var repository = new Mock<ITouristTaxRateRepository>();
        repository
            .Setup(r => r.GetActiveInPeriodAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rates);
        return new TouristTaxQuoteService(repository.Object, NullLogger<TouristTaxQuoteService>.Instance);
    }

    private static SeoContentService CreateService(ISeoContentRepository seoRepo, params TouristTaxRate[] rates) =>
        new(
            seoRepo,
            QuoteService(rates),
            Mock.Of<IAiProvider>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seo:PublicBaseUrl"] = "https://public.test",
            }).Build(),
            Mock.Of<ILogger<SeoContentService>>(),
            Today);

    private static TouristTaxRate ComoRate() => new()
    {
        City = "Como",
        IstatCode = "013075",
        RatePerPersonPerNight = 3.0m,
        MaxNights = 4,
        MinimumAge = 14,
        IsActive = true,
        EffectiveFrom = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task CalculateTouristTaxAsync_ComoRate_UsesTheEngineWithNightsCapAndAgeExemption()
    {
        var service = CreateService(Mock.Of<ISeoContentRepository>(), ComoRate());

        // 5 nights, cap 4; 2 adults + a 13-year-old (exempt under 14) + a 14-year-old: 3 x 3,00 x 4 = 36,00.
        var result = await service.CalculateTouristTaxAsync(new PublicTouristTaxCalculateRequest(
            "como",
            2,
            2,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 6, 0, 0, 0, DateTimeKind.Utc),
            ChildrenAges: [13, 14]));

        Assert.NotNull(result);
        Assert.Equal(TouristTaxQuoteStatus.Calculated, result!.Status);
        Assert.Equal(36m, result.TaxAmount);
        Assert.Equal(5, result.Nights);
        Assert.Equal(4, result.TaxableNights);
        Assert.True(result.AgeRulesApply);
    }

    [Fact]
    public async Task CalculateTouristTaxAsync_ComuneWithoutRate_ReturnsRateUnavailableNotAnError()
    {
        // A8-12: Palermo is in the registry but has no rate: an explicit state, no invented amount.
        var service = CreateService(Mock.Of<ISeoContentRepository>(), ComoRate());

        var result = await service.CalculateTouristTaxAsync(new PublicTouristTaxCalculateRequest(
            "palermo",
            2,
            0,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc)));

        Assert.NotNull(result);
        Assert.Equal(TouristTaxQuoteStatus.RateUnavailable, result!.Status);
        Assert.Null(result.TaxAmount);
    }

    [Fact]
    public async Task CalculateTouristTaxAsync_UnknownComune_ReturnsNull()
    {
        var service = CreateService(Mock.Of<ISeoContentRepository>(), ComoRate());

        var result = await service.CalculateTouristTaxAsync(new PublicTouristTaxCalculateRequest(
            "atlantide", 1, 0, new DateTime(2026, 7, 1), new DateTime(2026, 7, 2)));

        Assert.Null(result);
    }

    [Fact]
    public async Task BuildComplianceSitemapXmlAsync_CalculatorOfComuneWithoutRate_IsExcluded()
    {
        // A8-12: the tourist tax page of Palermo (no rate) is not indexed; its compliance guide still is.
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetReviewedPagesForSitemapAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new SeoContentPage { ComuneCode = "013075", PageType = SeoPageType.TouristTaxCalc, UpdatedAt = DateTime.UtcNow },
                new SeoContentPage { ComuneCode = "082053", PageType = SeoPageType.TouristTaxCalc, UpdatedAt = DateTime.UtcNow },
                new SeoContentPage { ComuneCode = "082053", PageType = SeoPageType.ComplianceGuide, UpdatedAt = DateTime.UtcNow },
            ]);
        var service = CreateService(seoRepo.Object, ComoRate());

        var xml = await service.BuildComplianceSitemapXmlAsync();

        Assert.Contains("https://public.test/p/tassa-soggiorno/como", xml);
        Assert.DoesNotContain("https://public.test/p/tassa-soggiorno/palermo", xml);
        Assert.Contains("https://public.test/p/affitti-brevi/sicilia/palermo", xml);
    }

    [Fact]
    public async Task GetTouristTaxPageAsync_RateStoredWithLowerCaseCity_ListsTheRateInForce()
    {
        // A8-23: an admin typing "como" must not hide the rate of the "Como" page.
        var page = new SeoContentPage
        {
            Id = Guid.NewGuid(),
            ComuneCode = "013075",
            PageType = SeoPageType.TouristTaxCalc,
            LegalReviewStatus = LegalReviewStatus.Reviewed,
        };
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetPublishedTouristTaxPageAsync("como", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        var rate = ComoRate();
        rate.City = " como ";
        rate.IstatCode = null;
        var service = CreateService(seoRepo.Object, rate);

        var dto = await service.GetTouristTaxPageAsync("como", allowDraft: false);

        var summary = Assert.Single(dto!.TouristTaxRates);
        Assert.Equal(3.0m, summary.RatePerPersonPerNight);
        Assert.Equal(14, summary.MinimumAge);
    }

    [Fact]
    public async Task GeneratePagesForComuneBatchAsync_BudgetExhausted_StopsBatchAtFirstRefusedCall()
    {
        // A8-07: the budget guard refuses the call BEFORE the provider (AiBudgetExceededException); the batch must stop
        // instead of trying (and paying for) every remaining page.
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        seoRepo.Setup(r => r.GetPublishedPageAsync(
                It.IsAny<SeoPageType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeoContentPage?)null);

        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            aiProvider.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<SeoContentService>>());

        var generated = await service.GeneratePagesForComuneBatchAsync(
            ItalianComuneRegistry.AllCodes.Take(3).ToList(),
            [SeoPageType.ComplianceGuide, SeoPageType.TouristTaxCalc],
            forceRegenerate: true);

        Assert.Equal(0, generated);
        aiProvider.Verify(
            a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        seoRepo.Verify(r => r.AddRevisionAsync(It.IsAny<SeoContentRevision>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RefreshStalePagesAsync_BudgetExhausted_StopsAtFirstRefusedCall()
    {
        var codes = ItalianComuneRegistry.AllCodes.Take(3).ToList();
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetPagesNeedingRefreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(codes.Select(code => new SeoContentPage
            {
                Id = Guid.NewGuid(),
                ComuneCode = code,
                PageType = SeoPageType.ComplianceGuide,
            }).ToList());

        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            aiProvider.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<SeoContentService>>());

        var refreshed = await service.RefreshStalePagesAsync();

        Assert.Equal(0, refreshed);
        aiProvider.Verify(
            a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpdateReviewStatus_BlocksPublish_WhenCounselRequiredAndNotApproved()
    {
        var pageId = Guid.NewGuid();
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetByIdAsync(pageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeoContentPage
            {
                Id = pageId,
                CounselRequired = true,
                LegalReviewStatus = LegalReviewStatus.Draft,
            });

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            Mock.Of<IAiProvider>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<SeoContentService>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateReviewStatusAsync(pageId, LegalReviewStatus.Reviewed, counselApproved: false));
    }

    [Fact]
    public async Task GeneratePagesForComuneBatchAsync_AiReturnsMaliciousHtml_StoresSanitizedRevision()
    {
        const string aiHtml =
            "<h2>CIN</h2><img src=x onerror=alert(1)><svg/onload=alert(1)>" +
            "<p onclick=\"alert(1)\">Testo <a href=\"&#106;avascript:alert(1)\">x</a></p>";
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        seoRepo.Setup(r => r.GetOrCreatePlatformAiBudgetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformAiBudget
            {
                MonthlyTokenCap = 100_000,
                LastResetAt = DateTime.UtcNow,
            });
        seoRepo.Setup(r => r.GetPublishedPageAsync(
                It.IsAny<SeoPageType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeoContentPage?)null);
        seoRepo.Setup(r => r.UpsertPageAsync(It.IsAny<SeoContentPage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeoContentPage p, CancellationToken _) => p);
        SeoContentRevision? stored = null;
        seoRepo.Setup(r => r.AddRevisionAsync(It.IsAny<SeoContentRevision>(), It.IsAny<CancellationToken>()))
            .Callback<SeoContentRevision, CancellationToken>((r, _) => stored = r)
            .Returns(Task.CompletedTask);

        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(a => a.GenerateAsync(
                It.IsAny<string>(),
                It.IsAny<AiModelTier>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(aiHtml, 10, 10, AiModelTier.Economy, FromCache: false));

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            aiProvider.Object,
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<SeoContentService>>());

        var generated = await service.GeneratePagesForComuneBatchAsync(
            ["013075"],
            [SeoPageType.ComplianceGuide],
            forceRegenerate: true);

        Assert.Equal(1, generated);
        Assert.NotNull(stored);
        Assert.Equal(
            "<h2>CIN</h2><p>Testo <a rel=\"noopener noreferrer\">x</a></p>",
            stored!.BodyHtml);
    }

    [Theory]
    [InlineData(SeoPageType.ComplianceGuide)]
    [InlineData(SeoPageType.TouristTaxCalc)]
    public async Task PublicPage_StoredRevisionWithMaliciousHtml_ReturnsSanitizedBody(SeoPageType pageType)
    {
        // Revisions stored before the allowlist (or written by any other path) are sanitized again on read.
        var page = new SeoContentPage
        {
            Id = Guid.NewGuid(),
            ComuneCode = "013075",
            PageType = pageType,
            Title = "Como",
            LegalReviewStatus = LegalReviewStatus.Reviewed,
        };
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetPublishedPageAsync(
                SeoPageType.ComplianceGuide, "lombardia", "como", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        seoRepo.Setup(r => r.GetPublishedTouristTaxPageAsync("como", false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        seoRepo.Setup(r => r.GetLatestRevisionAsync(page.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeoContentRevision
            {
                PageId = page.Id,
                BodyHtml = "<h2>Guida</h2><iframe src=\"https://evil.example\"></iframe>" +
                    "<ul><li ONMOUSEOVER=\"alert(1)\">CIN</li></ul><style>*{}</style>" +
                    "<p><a href=\"https://www.example.com\">fonte</a></p>",
            });

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            Mock.Of<IAiProvider>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<ILogger<SeoContentService>>());

        var dto = pageType == SeoPageType.ComplianceGuide
            ? await service.GetComplianceGuideAsync("lombardia", "como", allowDraft: false)
            : await service.GetTouristTaxPageAsync("como", allowDraft: false);

        Assert.NotNull(dto);
        Assert.Equal(
            "<h2>Guida</h2><ul><li>CIN</li></ul>" +
            "<p><a href=\"https://www.example.com\" rel=\"noopener noreferrer\">fonte</a></p>",
            dto!.BodyHtml);
    }
}
