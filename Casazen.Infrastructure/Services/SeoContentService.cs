using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class SeoContentService(
    ISeoContentRepository repository,
    ITouristTaxQuoteService touristTaxQuoteService,
    IAiProvider aiProvider,
    PublicSiteLinks publicSiteLinks,
    ILogger<SeoContentService> logger,
    TimeProvider? timeProvider = null) : ISeoContentService
{
    public const int CounselRequiredBatchSize = 100;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");

    public async Task<SeoPagePublicDto?> GetComplianceGuideAsync(
        string regionSlug,
        string comuneSlug,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.GetPublishedPageAsync(
            SeoPageType.ComplianceGuide,
            regionSlug,
            comuneSlug,
            cancellationToken);

        return page is null ? null : await MapPublicPageAsync(page, cancellationToken);
    }

    public async Task<SeoPagePublicDto?> GetTouristTaxPageAsync(
        string comuneSlug,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.GetPublishedTouristTaxPageAsync(comuneSlug, cancellationToken);
        return page is null ? null : await MapPublicPageAsync(page, cancellationToken);
    }

    public async Task<PublicTouristTaxCalculateResponse?> CalculateTouristTaxAsync(
        PublicTouristTaxCalculateRequest request,
        CancellationToken cancellationToken = default)
    {
        var comune = ItalianComuneRegistry.GetBySlug(request.ComuneSlug);
        if (comune is null)
            return null;

        // Same engine as the checkout (BK-03): ages, category, season, cap of nights and percentage rates included.
        var quote = await touristTaxQuoteService.QuoteAsync(
            ToTouristTaxComune(comune),
            new TouristTaxStay(
                RomeCalendar.DateInRome(request.CheckInDate),
                RomeCalendar.DateInRome(request.CheckOutDate),
                request.NumberOfAdults,
                request.NumberOfChildren,
                request.ChildrenAges,
                request.AccommodationCategory,
                request.NightlyPrice),
            cancellationToken);

        return new PublicTouristTaxCalculateResponse(
            comune.ComuneSlug,
            comune.Name,
            quote.Status,
            quote.Status == TouristTaxQuoteStatus.Calculated ? quote.Amount : null,
            request.NumberOfAdults,
            request.NumberOfChildren,
            quote.Nights,
            quote.TaxableNights,
            quote.AgeRulesApply,
            quote.Categories,
            request.CheckInDate,
            request.CheckOutDate);
    }

    public async Task<(IReadOnlyList<SeoPageAdminDto> Items, int TotalCount)> ListPagesAsync(
        LegalReviewStatus? legalReviewStatus,
        SeoPageType? pageType,
        string? comuneCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var (items, total) = await repository.ListPagesAsync(
            legalReviewStatus,
            pageType,
            comuneCode,
            page,
            pageSize,
            cancellationToken);

        return (items.Select(i => MapAdminPage(i.Page, i.LatestRevision, i.LastReviewEvent)).ToList(), total);
    }

    public async Task<SeoPageAdminDetailDto?> GetAdminPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        var page = await repository.GetByIdAsync(pageId, cancellationToken);
        if (page is null)
            return null;

        var latest = await repository.GetLatestRevisionAsync(page.Id, cancellationToken);
        var published = page.PublishedRevisionId is { } publishedId
            ? latest?.Id == publishedId ? latest : await repository.GetRevisionAsync(publishedId, cancellationToken)
            : null;
        var history = await repository.GetReviewEventsAsync(page.Id, cancellationToken);

        return new SeoPageAdminDetailDto(
            MapAdminPage(page, latest is null ? null : Summary(latest), history.FirstOrDefault()),
            published is null ? null : Preview(published),
            latest is not null && latest.Id != page.PublishedRevisionId ? Preview(latest) : null,
            history.Select(ToDto).ToList());
    }

    public async Task<SeoPageAdminDto> ApproveRevisionAsync(
        SeoApproveRevisionCommand command,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.GetByIdAsync(command.PageId, cancellationToken)
            ?? throw new NotFoundException($"SEO page {command.PageId} not found");

        // A8-04: the first pages need an explicit confirmation of the legal review, never a default.
        if (page.CounselRequired && !command.CounselApproved)
            throw new DomainRuleException(SeoReviewErrorCodes.CounselRequired, "SeoCounselRequired", CounselRequiredBatchSize);

        var outcome = await repository.ApproveRevisionAsync(
            new SeoContentReviewEvent
            {
                PageId = page.Id,
                RevisionId = command.RevisionId,
                Action = SeoReviewAction.Approved,
                ActorUserId = command.ActorUserId,
                OccurredAt = _clock.GetUtcNow().UtcDateTime,
                CounselApproved = command.CounselApproved,
                Note = NormalizeNote(command.Note),
            },
            cancellationToken);

        switch (outcome)
        {
            case SeoApprovalOutcome.PageNotFound:
                throw new NotFoundException($"SEO page {command.PageId} not found");
            case SeoApprovalOutcome.RevisionNotFound:
                throw new NotFoundException($"SEO revision {command.RevisionId} not found on page {command.PageId}");
            case SeoApprovalOutcome.RevisionOutdated:
                throw new DomainConflictException(SeoReviewErrorCodes.RevisionOutdated, "SeoRevisionOutdated");
            case SeoApprovalOutcome.AlreadyPublished:
                throw new DomainConflictException(SeoReviewErrorCodes.RevisionAlreadyPublished, "SeoRevisionAlreadyPublished");
            case SeoApprovalOutcome.NotPublishable:
                throw new DomainRuleException(SeoReviewErrorCodes.RevisionNotPublishable, "SeoRevisionNotPublishable");
        }

        logger.LogInformation(
            "SEO page {PageId} revision {RevisionId} approved by {ActorUserId}",
            page.Id, command.RevisionId, command.ActorUserId);
        return await GetAdminPageDtoAsync(page.Id, cancellationToken);
    }

    public async Task<SeoPageAdminDto> WithdrawAsync(SeoWithdrawCommand command, CancellationToken cancellationToken = default)
    {
        var outcome = await repository.WithdrawAsync(
            new SeoContentReviewEvent
            {
                PageId = command.PageId,
                Action = SeoReviewAction.Withdrawn,
                ActorUserId = command.ActorUserId,
                OccurredAt = _clock.GetUtcNow().UtcDateTime,
                Note = NormalizeNote(command.Note),
            },
            cancellationToken);

        switch (outcome)
        {
            case SeoWithdrawOutcome.PageNotFound:
                throw new NotFoundException($"SEO page {command.PageId} not found");
            case SeoWithdrawOutcome.NotPublished:
                throw new DomainConflictException(SeoReviewErrorCodes.PageNotPublished, "SeoPageNotPublished");
        }

        logger.LogInformation("SEO page {PageId} withdrawn by {ActorUserId}", command.PageId, command.ActorUserId);
        return await GetAdminPageDtoAsync(command.PageId, cancellationToken);
    }

    private async Task<SeoPageAdminDto> GetAdminPageDtoAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var detail = await GetAdminPageAsync(pageId, cancellationToken)
            ?? throw new NotFoundException($"SEO page {pageId} not found");
        return detail.Page;
    }

    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public async Task<PlatformAiBudgetDto> GetPlatformAiBudgetAsync(CancellationToken cancellationToken = default)
    {
        var budget = await repository.GetOrCreatePlatformAiBudgetAsync(cancellationToken);
        await ResetBudgetIfNeededAsync(budget, cancellationToken);
        return new PlatformAiBudgetDto(budget.MonthlyTokenCap, budget.TokensUsedThisMonth, budget.LastResetAt);
    }

    public async Task<int> GeneratePagesForComuneBatchAsync(
        IReadOnlyList<string> comuneCodes,
        IReadOnlyList<SeoPageType> pageTypes,
        bool forceRegenerate,
        CancellationToken cancellationToken = default)
    {
        var generated = 0;
        var totalPagesBefore = await repository.CountAllPagesAsync(cancellationToken);

        foreach (var comuneCode in comuneCodes)
        {
            var comune = ItalianComuneRegistry.GetByCode(comuneCode);
            if (comune is null)
            {
                logger.LogWarning("Unknown comune code {ComuneCode}; skipping SEO generation", comuneCode);
                continue;
            }

            foreach (var pageType in pageTypes)
            {
                if (pageType == SeoPageType.SupplierMicrosite)
                {
                    logger.LogInformation("SupplierMicrosite deferred — skipping {Comune}", comune.Name);
                    continue;
                }

                try
                {
                    var created = await GenerateSinglePageAsync(
                        comune,
                        pageType,
                        forceRegenerate,
                        totalPagesBefore + generated,
                        cancellationToken);

                    if (created)
                        generated++;
                }
                catch (AiBudgetExceededException)
                {
                    // A8-07: the provider was not called; every following page would hit the same cap, so stop here.
                    logger.LogWarning(
                        "Platform AI budget exhausted: SEO batch stopped at {Comune} {PageType} after {Generated} pages",
                        comune.Name, pageType, generated);
                    return generated;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "SEO generation failed for {Comune} {PageType}", comune.Name, pageType);
                }
            }
        }

        return generated;
    }

    public async Task<int> RefreshStalePagesAsync(CancellationToken cancellationToken = default)
    {
        var stalePages = await repository.GetPagesNeedingRefreshAsync(cancellationToken);
        var refreshed = 0;

        foreach (var page in stalePages)
        {
            var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode);
            if (comune is null)
                continue;

            try
            {
                var created = await GenerateSinglePageAsync(
                    comune,
                    page.PageType,
                    forceRegenerate: true,
                    ordinalHint: await repository.CountAllPagesAsync(cancellationToken),
                    cancellationToken);

                if (created)
                    refreshed++;
            }
            catch (AiBudgetExceededException)
            {
                logger.LogWarning(
                    "Platform AI budget exhausted: SEO refresh stopped at page {PageId} after {Refreshed} pages",
                    page.Id, refreshed);
                return refreshed;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SEO refresh failed for page {PageId}", page.Id);
            }
        }

        return refreshed;
    }

    public async Task<SeoPublishedPagesDto> GetPublishedPagesAsync(CancellationToken cancellationToken = default)
    {
        var pages = await GetIndexablePagesAsync(cancellationToken);
        return new SeoPublishedPagesDto(
            publicSiteLinks.TryPublicPage(SeoPagePaths.Hub),
            pages.Select(p => new SeoPublishedPageDto(
                    p.Page.PageType,
                    p.Page.Title,
                    p.Comune.Name,
                    p.Comune.RegionSlug,
                    p.Comune.ComuneSlug,
                    p.Path))
                .ToList());
    }

    public async Task<string> BuildComplianceSitemapXmlAsync(CancellationToken cancellationToken = default)
    {
        // SE-02 (A8-02, D3): every URL is on App:PublicSiteBaseUrl, the domain of the web app that serves /p/*.
        // Missing value: configuration error (only possible in Development/Testing), never a fallback domain.
        publicSiteLinks.EnsureConfigured();

        var pages = await GetIndexablePagesAsync(cancellationToken);
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        var urlset = new XElement(ns + "urlset");
        if (pages.Count > 0)
        {
            urlset.Add(SitemapUrl(ns, SeoPagePaths.Hub, pages.Max(p => p.LastModified)));
            foreach (var page in pages)
                urlset.Add(SitemapUrl(ns, page.Path, page.LastModified));
        }

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), urlset);
        return document.Declaration + Environment.NewLine + document;
    }

    private XElement SitemapUrl(XNamespace ns, string path, DateTime lastModified) =>
        new(ns + "url",
            new XElement(ns + "loc", publicSiteLinks.PublicPage(path)),
            new XElement(ns + "lastmod", lastModified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The pages worth indexing, shared by the sitemap and the hub: published (an approved revision, SE-01), with content,
    /// of a known comune and, for a calculator, with a tourist tax rate in force. There is no feature flag on the SEO pages.
    /// The last modification is the approval of the published text, not a later draft.
    /// </summary>
    private async Task<IReadOnlyList<IndexablePage>> GetIndexablePagesAsync(CancellationToken cancellationToken)
    {
        var candidates = await repository.GetPublishedPagesForSitemapAsync(cancellationToken);
        var today = _clock.TodayInRomeAsDateOnly();
        var pages = new List<IndexablePage>(candidates.Count);
        foreach (var page in candidates)
        {
            var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode);
            if (comune is null)
                continue;

            // A8-12 (BK-03): a calculator page of a comune without a rate in force is not worth indexing.
            if (page.PageType == SeoPageType.TouristTaxCalc
                && (await touristTaxQuoteService.GetRatesInForceAsync(ToTouristTaxComune(comune), today, cancellationToken)).Count == 0)
            {
                continue;
            }

            pages.Add(new IndexablePage(
                page,
                comune,
                SeoPagePaths.For(comune, page.PageType),
                page.PublishedAt ?? page.PublishedRevision?.GeneratedAt ?? page.UpdatedAt));
        }

        return pages;
    }

    private sealed record IndexablePage(SeoContentPage Page, ComuneInfo Comune, string Path, DateTime LastModified);

    /// <summary>
    /// Stores a new <b>draft</b> revision of the page (SE-01): never approved here, and the published revision, if any,
    /// stays the one the public sees until an admin approves the new text. An answer that is not publishable (no provider
    /// configured, empty, invalid) is stored as an explicit "content not generated" revision. Returns false when the
    /// page is already up to date (same data, same prompt, publishable text).
    /// </summary>
    private async Task<bool> GenerateSinglePageAsync(
        ComuneInfo comune,
        SeoPageType pageType,
        bool forceRegenerate,
        int ordinalHint,
        CancellationToken cancellationToken)
    {
        var taxRates = await touristTaxQuoteService.GetRatesInForceAsync(
            ToTouristTaxComune(comune), _clock.TodayInRomeAsDateOnly(), cancellationToken);
        var sourceVersion = BuildSourceDataVersion(comune, taxRates);

        var slug = BuildSlug(comune, pageType);
        var existing = await repository.GetPageAsync(comune.Code, pageType, cancellationToken);

        if (existing is not null && !forceRegenerate)
        {
            var latest = await repository.GetLatestRevisionAsync(existing.Id, cancellationToken);
            if (latest is { ContentStatus: SeoContentStatus.Generated, PromptVersion: SeoContentPrompt.Version }
                && latest.SourceDataVersion == sourceVersion)
            {
                logger.LogInformation("SEO source unchanged for {Slug}; skipping LLM", slug);
                return false;
            }
        }

        var cacheKey = $"{comune.Code}:{pageType}:{sourceVersion}:{SeoContentPrompt.Version}";
        var prompt = SeoContentPrompt.Build(comune, pageType, taxRates);
        // A paid provider is wrapped by the platform budget guard: it checks the cap BEFORE the call and throws
        // AiBudgetExceededException, which stops the batch (A8-07). The prompt holds public regulatory data only.
        var aiResult = await aiProvider.GenerateAsync(prompt, AiModelTier.Economy, cacheKey, cancellationToken);
        var (contentStatus, bodyHtml) = SeoGeneratedContent.Evaluate(aiResult);
        if (contentStatus != SeoContentStatus.Generated)
        {
            logger.LogWarning(
                "SEO content not generated for {Slug}: {ContentStatus}; stored as a draft that cannot be approved",
                slug, contentStatus);
        }

        var page = existing ?? new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comune.Code,
            RegionCode = comune.RegionCode,
            PageType = pageType,
            CounselRequired = ordinalHint < CounselRequiredBatchSize,
        };

        var now = _clock.GetUtcNow().UtcDateTime;
        page.Slug = slug;
        page.Title = BuildTitle(comune, pageType);
        page.MetaDescription = BuildMetaDescription(comune, pageType);
        page.LastRefreshedAt = now;
        page = await repository.UpsertPageAsync(page, cancellationToken);

        await repository.AddRevisionAsync(new SeoContentRevision
        {
            PageId = page.Id,
            BodyHtml = bodyHtml,
            ContentStatus = contentStatus,
            PromptVersion = SeoContentPrompt.Version,
            AiModelTier = AiModelTier.Economy,
            PromptTokens = aiResult.FromCache || !aiResult.ProviderConfigured ? 0 : aiResult.PromptTokens,
            GeneratedAt = now,
            SourceDataVersion = sourceVersion,
        }, cancellationToken);

        return true;
    }

    private async Task<SeoPagePublicDto> MapPublicPageAsync(SeoContentPage page, CancellationToken cancellationToken)
    {
        var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode)
            ?? throw new InvalidOperationException($"Unknown comune code {page.ComuneCode}");

        // SE-01 (A8-05): the approved revision only, never a newer draft. Sanitized again on read (FD-15).
        var revision = page.PublishedRevision
            ?? (page.PublishedRevisionId is { } publishedId
                ? await repository.GetRevisionAsync(publishedId, cancellationToken)
                : null);
        var bodyHtml = SeoHtmlSanitizer.Sanitize(revision?.BodyHtml);
        var refreshedAt = revision?.GeneratedAt;
        var taxRates = await touristTaxQuoteService.GetRatesInForceAsync(
            ToTouristTaxComune(comune), _clock.TodayInRomeAsDateOnly(), cancellationToken);

        return new SeoPagePublicDto(
            page.Id,
            page.PageType,
            page.Title,
            page.MetaDescription,
            bodyHtml,
            comune.Name,
            comune.Code,
            comune.RegionCode,
            comune.RegionSlug,
            comune.ComuneSlug,
            BuildCanonicalUrl(comune, page.PageType),
            refreshedAt,
            BuildDisclaimers(refreshedAt),
            BuildCta(comune, page.PageType),
            taxRates.Select(ToPublicSummary).ToList());
    }

    /// <summary>The registry gives a trusted ISTAT code: rates carrying one are matched by code, the others by name.</summary>
    private static TouristTaxComune ToTouristTaxComune(ComuneInfo comune) => new(comune.Code, comune.Name);

    private static PublicTouristTaxRateSummaryDto ToPublicSummary(TouristTaxRate rate) =>
        new(
            rate.City,
            rate.AccommodationCategory,
            rate.SeasonStart,
            rate.SeasonEnd,
            rate.CalculationMethod,
            rate.RatePerPersonPerNight,
            rate.PercentOfNightlyPrice,
            rate.CapPerPersonPerNight,
            rate.MaxNights,
            rate.MinimumAge,
            rate.ReducedRateMaxAge,
            rate.ReducedRatePerPersonPerNight,
            rate.EffectiveFrom,
            rate.EffectiveTo,
            rate.SourceUrl);

    private SeoPageAdminDto MapAdminPage(
        SeoContentPage page,
        SeoRevisionSummary? latest,
        SeoReviewEventSummary? lastReviewEvent)
    {
        var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode);
        var publicPath = comune is null ? null : SeoPagePaths.For(comune, page.PageType);

        return new SeoPageAdminDto(
            page.Id,
            page.Slug,
            page.ComuneCode,
            comune?.Name ?? page.ComuneCode,
            page.RegionCode,
            comune?.RegionSlug ?? string.Empty,
            page.PageType,
            page.Title,
            page.LegalReviewStatus,
            page.PublishedAt,
            page.LastRefreshedAt,
            latest is null
                ? null
                : new SeoRevisionAdminDto(
                    latest.Id,
                    latest.GeneratedAt,
                    latest.AiModelTier.ToString(),
                    latest.PromptTokens,
                    latest.SourceDataVersion,
                    latest.ContentStatus,
                    latest.PromptVersion),
            page.PublishedRevisionId,
            page.PublishedRevisionId is not null,
            latest is not null && latest.Id != page.PublishedRevisionId,
            page.CounselRequired,
            publicPath,
            publicPath is null ? null : publicSiteLinks.TryPublicPage(publicPath),
            lastReviewEvent is null ? null : ToDto(lastReviewEvent));
    }

    private static SeoRevisionSummary Summary(SeoContentRevision revision) =>
        new(
            revision.Id,
            revision.GeneratedAt,
            revision.AiModelTier,
            revision.PromptTokens,
            revision.SourceDataVersion,
            revision.ContentStatus,
            revision.PromptVersion);

    /// <summary>The text as the public page would show it: sanitized with the FD-15 allowlist.</summary>
    private static SeoRevisionPreviewDto Preview(SeoContentRevision revision) =>
        new(
            revision.Id,
            revision.GeneratedAt,
            revision.ContentStatus,
            revision.PromptVersion,
            revision.SourceDataVersion,
            SeoHtmlSanitizer.Sanitize(revision.BodyHtml));

    private static SeoReviewEventDto ToDto(SeoReviewEventSummary reviewEvent) =>
        new(
            reviewEvent.Action,
            reviewEvent.RevisionId,
            reviewEvent.ActorUserId,
            reviewEvent.OccurredAt,
            reviewEvent.CounselApproved,
            reviewEvent.Note);

    /// <summary>
    /// Changes when a rate of the comune changes. With zero or one rate it keeps the pre-BK-03 format, so existing pages
    /// are not regenerated just because the format changed. With several rates (a comune with seasonal or per-category
    /// tariffs) the raw format grows without bound, past the column's 100 characters (SE-01): hashed to a fixed length
    /// instead, still a valid change-detection token even if not human-readable.
    /// </summary>
    private static string BuildSourceDataVersion(ComuneInfo comune, IReadOnlyList<TouristTaxRate> taxRates)
    {
        var first = taxRates.FirstOrDefault();
        var version = $"{comune.Code}:{first?.UpdatedAt:O}:{first?.RatePerPersonPerNight}:{first?.MaxNights}";
        if (taxRates.Count <= 1)
            return version;

        foreach (var rate in taxRates.Skip(1))
            version += $"|{rate.Id:N}:{rate.UpdatedAt:O}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(version));
        return $"{comune.Code}:{taxRates.Count}:{Convert.ToHexStringLower(hash)}";
    }

    private static string BuildSlug(ComuneInfo comune, SeoPageType pageType) =>
        pageType switch
        {
            SeoPageType.ComplianceGuide => $"affitti-brevi/{comune.RegionSlug}/{comune.ComuneSlug}",
            SeoPageType.TouristTaxCalc => $"tassa-soggiorno/{comune.ComuneSlug}",
            _ => $"supplier/{comune.ComuneSlug}",
        };

    private static string BuildTitle(ComuneInfo comune, SeoPageType pageType) =>
        pageType switch
        {
            SeoPageType.ComplianceGuide => $"Affitti brevi a {comune.Name}: CIN e tassa di soggiorno",
            SeoPageType.TouristTaxCalc => $"Tassa di soggiorno a {comune.Name}",
            _ => $"Fornitori per affitti brevi a {comune.Name}",
        };

    private static string BuildMetaDescription(ComuneInfo comune, SeoPageType pageType) =>
        pageType switch
        {
            SeoPageType.ComplianceGuide =>
                $"Guida aggiornata per host di affitti brevi a {comune.Name}: CIN, Alloggiati Web e tassa di soggiorno.",
            SeoPageType.TouristTaxCalc =>
                $"Calcola la tassa di soggiorno a {comune.Name} con le tariffe ufficiali del comune.",
            _ => $"Microsite fornitori per affitti brevi a {comune.Name} (Phase 0 deferred).",
        };

    /// <summary>Canonical URL on App:PublicSiteBaseUrl (D3); null only when it is not configured (Development/Testing).</summary>
    private string? BuildCanonicalUrl(ComuneInfo comune, SeoPageType pageType) =>
        publicSiteLinks.TryPublicPage(SeoPagePaths.For(comune, pageType));

    private static SeoDisclaimersDto BuildDisclaimers(DateTime? refreshedAt)
    {
        var dateText = refreshedAt.HasValue
            ? refreshedAt.Value.ToString("d MMMM yyyy", ItalianCulture)
            : "data non disponibile";

        return new SeoDisclaimersDto(
            $"Ultimo aggiornamento: {dateText}",
            "Informazione generale, non consulenza legale. L'host resta responsabile degli adempimenti.",
            "Contenuto generato con AI — verifica le fonti ufficiali");
    }

    /// <summary>
    /// Signup CTA on App:PublicSiteBaseUrl (D3, SE-03); relative only when it is not configured (Development/Testing).
    /// No compliance checker CTA: the tool has no spec yet (A8-03).
    /// </summary>
    private SeoCtaDto BuildCta(ComuneInfo comune, SeoPageType pageType)
    {
        var path = SeoPagePaths.SignupCta(comune, pageType);
        return new SeoCtaDto(publicSiteLinks.TryPublicPage(path) ?? path);
    }

    private async Task ResetBudgetIfNeededAsync(PlatformAiBudget budget, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (budget.LastResetAt.Month == now.Month && budget.LastResetAt.Year == now.Year)
            return;

        budget.TokensUsedThisMonth = 0;
        budget.LastResetAt = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        await repository.SavePlatformAiBudgetAsync(budget, cancellationToken);
    }
}
