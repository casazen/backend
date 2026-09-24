using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SeoContentServiceTests
{
    [Fact]
    public async Task CalculateTax_UsesTouristTaxRateEntity_NotHardcoded()
    {
        var comune = ItalianComuneRegistry.GetBySlug("como")!;
        var taxRate = new TouristTaxRate
        {
            City = comune.Name,
            RatePerPersonPerNight = 3.0m,
            MaxNights = 5,
            IsActive = true,
            EffectiveFrom = DateTime.UtcNow.AddYears(-1),
        };

        var touristTaxRepo = new Mock<ITouristTaxRateRepository>();
        touristTaxRepo
            .Setup(r => r.GetActiveByCityAsync(comune.Name, It.IsAny<DateTime>()))
            .ReturnsAsync(taxRate);

        var touristTaxService = new Mock<ITouristTaxService>();
        touristTaxService
            .Setup(s => s.CalculateTouristTaxAsync(comune.Name, 2, 0, It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(24m);

        var seoRepo = new Mock<ISeoContentRepository>();
        var aiProvider = new Mock<IAiProvider>();
        var config = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SeoContentService>>();

        var service = new SeoContentService(
            seoRepo.Object,
            touristTaxRepo.Object,
            touristTaxService.Object,
            aiProvider.Object,
            config,
            logger.Object);

        var result = await service.CalculateTouristTaxAsync(new PublicTouristTaxCalculateRequest(
            "como",
            2,
            0,
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc)));

        Assert.NotNull(result);
        Assert.Equal(24m, result!.TaxAmount);
        Assert.Equal(3.0m, result.RatePerPersonPerNight);
    }

    [Fact]
    public async Task GeneratePages_StopsWhenPlatformAiBudgetExceeded()
    {
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        seoRepo.Setup(r => r.GetOrCreatePlatformAiBudgetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlatformAiBudget { MonthlyTokenCap = 100, TokensUsedThisMonth = 95 });
        seoRepo.Setup(r => r.GetPublishedPageAsync(
                It.IsAny<SeoPageType>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeoContentPage?)null);

        var touristTaxRepo = new Mock<ITouristTaxRateRepository>();
        touristTaxRepo
            .Setup(r => r.GetActiveByCityAsync("Como", It.IsAny<DateTime>()))
            .ReturnsAsync(new TouristTaxRate { City = "Como", RatePerPersonPerNight = 2m, IsActive = true });

        var aiProvider = new StubAiProvider(Mock.Of<ILogger<StubAiProvider>>());
        var config = new ConfigurationBuilder().Build();
        var logger = new Mock<ILogger<SeoContentService>>();

        var service = new SeoContentService(
            seoRepo.Object,
            touristTaxRepo.Object,
            Mock.Of<ITouristTaxService>(),
            aiProvider,
            config,
            logger.Object);

        var generated = await service.GeneratePagesForComuneBatchAsync(
            ["013075"],
            [SeoPageType.ComplianceGuide],
            forceRegenerate: true);

        Assert.Equal(0, generated);
        seoRepo.Verify(r => r.AddRevisionAsync(It.IsAny<SeoContentRevision>(), It.IsAny<CancellationToken>()), Times.Never);
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
            Mock.Of<ITouristTaxRateRepository>(),
            Mock.Of<ITouristTaxService>(),
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
            Mock.Of<ITouristTaxRateRepository>(),
            Mock.Of<ITouristTaxService>(),
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
            Mock.Of<ITouristTaxRateRepository>(),
            Mock.Of<ITouristTaxService>(),
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
