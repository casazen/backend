using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
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
            EmailTestHelpers.Links("https://public.test"),
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
        seoRepo.Setup(r => r.GetPublishedPagesForSitemapAsync(It.IsAny<CancellationToken>()))
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
        seoRepo.Setup(r => r.GetPublishedTouristTaxPageAsync("como", It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        var rate = ComoRate();
        rate.City = " como ";
        rate.IstatCode = null;
        var service = CreateService(seoRepo.Object, rate);

        var dto = await service.GetTouristTaxPageAsync("como");

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

        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AiBudgetExceededException());

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            aiProvider.Object,
            EmailTestHelpers.Links(),
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
            EmailTestHelpers.Links(),
            Mock.Of<ILogger<SeoContentService>>());

        var refreshed = await service.RefreshStalePagesAsync();

        Assert.Equal(0, refreshed);
        aiProvider.Verify(
            a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApproveRevisionAsync_CounselRequiredWithoutConfirmation_ThrowsCounselRequiredAndPublishesNothing()
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
        var service = CreateService(seoRepo.Object);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.ApproveRevisionAsync(
            new SeoApproveRevisionCommand(pageId, Guid.NewGuid(), CounselApproved: false, Note: null, "auth0|admin")));

        Assert.Equal(SeoReviewErrorCodes.CounselRequired, error.Code);
        seoRepo.Verify(
            r => r.ApproveRevisionAsync(It.IsAny<SeoContentReviewEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(SeoApprovalOutcome.RevisionOutdated, typeof(DomainConflictException), SeoReviewErrorCodes.RevisionOutdated)]
    [InlineData(SeoApprovalOutcome.AlreadyPublished, typeof(DomainConflictException), SeoReviewErrorCodes.RevisionAlreadyPublished)]
    [InlineData(SeoApprovalOutcome.NotPublishable, typeof(DomainRuleException), SeoReviewErrorCodes.RevisionNotPublishable)]
    public async Task ApproveRevisionAsync_RefusedByRepository_ThrowsStableCode(
        SeoApprovalOutcome outcome, Type exceptionType, string code)
    {
        var pageId = Guid.NewGuid();
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetByIdAsync(pageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeoContentPage { Id = pageId, CounselRequired = true });
        seoRepo.Setup(r => r.ApproveRevisionAsync(It.IsAny<SeoContentReviewEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(outcome);
        var service = CreateService(seoRepo.Object);

        var error = await Assert.ThrowsAnyAsync<DomainException>(() => service.ApproveRevisionAsync(
            new SeoApproveRevisionCommand(pageId, Guid.NewGuid(), CounselApproved: true, Note: "ok", "auth0|admin")));

        Assert.IsType(exceptionType, error);
        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task ApproveRevisionAsync_Approved_WritesAuditWithActorRevisionTimeAndTrimmedNote()
    {
        var pageId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetByIdAsync(pageId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeoContentPage { Id = pageId, ComuneCode = "013075", CounselRequired = true });
        SeoContentReviewEvent? audit = null;
        seoRepo.Setup(r => r.ApproveRevisionAsync(It.IsAny<SeoContentReviewEvent>(), It.IsAny<CancellationToken>()))
            .Callback<SeoContentReviewEvent, CancellationToken>((e, _) => audit = e)
            .ReturnsAsync(SeoApprovalOutcome.Approved);
        seoRepo.Setup(r => r.GetReviewEventsAsync(pageId, It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = CreateService(seoRepo.Object);

        await service.ApproveRevisionAsync(
            new SeoApproveRevisionCommand(pageId, revisionId, CounselApproved: true, Note: "  Revisionato  ", "auth0|admin"));

        Assert.NotNull(audit);
        Assert.Equal(SeoReviewAction.Approved, audit!.Action);
        Assert.Equal(revisionId, audit.RevisionId);
        Assert.Equal("auth0|admin", audit.ActorUserId);
        Assert.True(audit.CounselApproved);
        Assert.Equal("Revisionato", audit.Note);
        Assert.Equal(Today.GetUtcNow().UtcDateTime, audit.OccurredAt);
    }

    [Fact]
    public async Task WithdrawAsync_PageNotPublished_ThrowsConflict()
    {
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.WithdrawAsync(It.IsAny<SeoContentReviewEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SeoWithdrawOutcome.NotPublished);
        var service = CreateService(seoRepo.Object);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() =>
            service.WithdrawAsync(new SeoWithdrawCommand(Guid.NewGuid(), null, "auth0|admin")));

        Assert.Equal(SeoReviewErrorCodes.PageNotPublished, error.Code);
    }

    [Fact]
    public async Task GeneratePagesForComuneBatchAsync_StubProvider_StoresNotGeneratedDraftWithoutBody()
    {
        var seoRepo = GenerationRepository(out var stored);
        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            new StubAiProvider(NullLogger<StubAiProvider>.Instance),
            EmailTestHelpers.Links(),
            Mock.Of<ILogger<SeoContentService>>(),
            Today);

        var generated = await service.GeneratePagesForComuneBatchAsync(["013075"], [SeoPageType.ComplianceGuide], forceRegenerate: true);

        Assert.Equal(1, generated);
        var revision = Assert.Single(stored);
        Assert.Equal(SeoContentStatus.AiProviderNotConfigured, revision.ContentStatus);
        Assert.Equal(string.Empty, revision.BodyHtml);
        Assert.Equal(0, revision.PromptTokens);
        Assert.Equal(SeoContentPrompt.Version, revision.PromptVersion);
        seoRepo.Verify(r => r.ApproveRevisionAsync(It.IsAny<SeoContentReviewEvent>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GeneratePagesForComuneBatchAsync_ValidAnswer_StoresGeneratedDraftAndNeverApproves()
    {
        var seoRepo = GenerationRepository(out var stored);
        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(SeoGeneratedContentTests.ValidHtml(), 50, 500, AiModelTier.Economy, FromCache: false));
        var service = new SeoContentService(
            seoRepo.Object, QuoteService(), aiProvider.Object, EmailTestHelpers.Links(),
            Mock.Of<ILogger<SeoContentService>>(), Today);

        await service.GeneratePagesForComuneBatchAsync(["013075"], [SeoPageType.ComplianceGuide], forceRegenerate: true);

        var revision = Assert.Single(stored);
        Assert.Equal(SeoContentStatus.Generated, revision.ContentStatus);
        Assert.Equal(50, revision.PromptTokens);
        seoRepo.Verify(r => r.ApproveRevisionAsync(It.IsAny<SeoContentReviewEvent>(), It.IsAny<CancellationToken>()), Times.Never);
        seoRepo.Verify(
            r => r.UpsertPageAsync(It.Is<SeoContentPage>(p => p.PublishedRevisionId == null && p.LegalReviewStatus == LegalReviewStatus.Draft), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GeneratePagesForComuneBatchAsync_LatestNotGenerated_RetriesEvenWithUnchangedData()
    {
        // A page left "not generated" (stub, missing key) is generated again once a provider is configured.
        var seoRepo = GenerationRepository(out var stored);
        var existing = new SeoContentPage { Id = Guid.NewGuid(), ComuneCode = "013075", PageType = SeoPageType.ComplianceGuide };
        seoRepo.Setup(r => r.GetPageAsync("013075", SeoPageType.ComplianceGuide, It.IsAny<CancellationToken>())).ReturnsAsync(existing);
        seoRepo.Setup(r => r.GetLatestRevisionAsync(existing.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SeoContentRevision
            {
                PageId = existing.Id,
                ContentStatus = SeoContentStatus.AiProviderNotConfigured,
                PromptVersion = SeoContentPrompt.Version,
                SourceDataVersion = "013075:::",
            });
        var aiProvider = new Mock<IAiProvider>();
        aiProvider
            .Setup(a => a.GenerateAsync(It.IsAny<string>(), It.IsAny<AiModelTier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiGenerationResult(SeoGeneratedContentTests.ValidHtml(), 50, 500, AiModelTier.Economy, FromCache: false));
        var service = new SeoContentService(
            seoRepo.Object, QuoteService(), aiProvider.Object, EmailTestHelpers.Links(),
            Mock.Of<ILogger<SeoContentService>>(), Today);

        var generated = await service.GeneratePagesForComuneBatchAsync(["013075"], [SeoPageType.ComplianceGuide], forceRegenerate: false);

        Assert.Equal(1, generated);
        Assert.Equal(SeoContentStatus.Generated, Assert.Single(stored).ContentStatus);
    }

    /// <summary>A repository for a generation from scratch, collecting the stored revisions.</summary>
    private static Mock<ISeoContentRepository> GenerationRepository(out List<SeoContentRevision> stored)
    {
        var revisions = new List<SeoContentRevision>();
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.CountAllPagesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(0);
        seoRepo.Setup(r => r.UpsertPageAsync(It.IsAny<SeoContentPage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SeoContentPage p, CancellationToken _) => p);
        seoRepo.Setup(r => r.AddRevisionAsync(It.IsAny<SeoContentRevision>(), It.IsAny<CancellationToken>()))
            .Callback<SeoContentRevision, CancellationToken>((r, _) => revisions.Add(r))
            .Returns(Task.CompletedTask);
        stored = revisions;
        return seoRepo;
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
            EmailTestHelpers.Links(),
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
        // Too short to be a page: kept (sanitized) for the admin, never publishable (A8-06).
        Assert.Equal(SeoContentStatus.InvalidOutput, stored.ContentStatus);
    }

    [Theory]
    [InlineData(SeoPageType.ComplianceGuide)]
    [InlineData(SeoPageType.TouristTaxCalc)]
    public async Task PublicPage_StoredRevisionWithMaliciousHtml_ReturnsSanitizedBody(SeoPageType pageType)
    {
        // Revisions stored before the allowlist (or written by any other path) are sanitized again on read.
        var revision = new SeoContentRevision
        {
            BodyHtml = "<h2>Guida</h2><iframe src=\"https://evil.example\"></iframe>" +
                "<ul><li ONMOUSEOVER=\"alert(1)\">CIN</li></ul><style>*{}</style>" +
                "<p><a href=\"https://www.example.com\">fonte</a></p>",
        };
        var page = PublishedPage(pageType, revision);
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetPublishedPageAsync(
                SeoPageType.ComplianceGuide, "lombardia", "como", It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        seoRepo.Setup(r => r.GetPublishedTouristTaxPageAsync("como", It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var service = new SeoContentService(
            seoRepo.Object,
            QuoteService(),
            Mock.Of<IAiProvider>(),
            EmailTestHelpers.Links(),
            Mock.Of<ILogger<SeoContentService>>());

        var dto = pageType == SeoPageType.ComplianceGuide
            ? await service.GetComplianceGuideAsync("lombardia", "como")
            : await service.GetTouristTaxPageAsync("como");

        Assert.NotNull(dto);
        Assert.Equal(
            "<h2>Guida</h2><ul><li>CIN</li></ul>" +
            "<p><a href=\"https://www.example.com\" rel=\"noopener noreferrer\">fonte</a></p>",
            dto!.BodyHtml);
    }

    [Fact]
    public async Task PublicPage_WithNewerDraft_ServesThePublishedRevisionAndItsDate()
    {
        // A8-05: the public page shows the approved text; the repository never hands it the latest draft.
        var published = new SeoContentRevision
        {
            BodyHtml = "<p>Testo approvato</p>",
            GeneratedAt = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
        };
        var page = PublishedPage(SeoPageType.ComplianceGuide, published);
        page.LastRefreshedAt = new DateTime(2026, 9, 20, 9, 0, 0, DateTimeKind.Utc);
        var seoRepo = new Mock<ISeoContentRepository>();
        seoRepo.Setup(r => r.GetPublishedPageAsync(
                SeoPageType.ComplianceGuide, "lombardia", "como", It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        var service = CreateService(seoRepo.Object);

        var dto = await service.GetComplianceGuideAsync("lombardia", "como");

        Assert.Equal("<p>Testo approvato</p>", dto!.BodyHtml);
        Assert.Equal(published.GeneratedAt, dto.LastRefreshedAt);
        seoRepo.Verify(r => r.GetLatestRevisionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static SeoContentPage PublishedPage(SeoPageType pageType, SeoContentRevision revision)
    {
        var page = new SeoContentPage
        {
            Id = Guid.NewGuid(),
            ComuneCode = "013075",
            PageType = pageType,
            Title = "Como",
            LegalReviewStatus = LegalReviewStatus.Reviewed,
        };
        revision.PageId = page.Id;
        page.PublishedRevisionId = revision.Id;
        page.PublishedRevision = revision;
        return page;
    }
}
