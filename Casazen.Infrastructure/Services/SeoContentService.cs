using System.Globalization;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class SeoContentService(
    ISeoContentRepository repository,
    ITouristTaxQuoteService touristTaxQuoteService,
    IAiProvider aiProvider,
    IConfiguration configuration,
    ILogger<SeoContentService> logger,
    TimeProvider? timeProvider = null) : ISeoContentService
{
    public const int CounselRequiredBatchSize = 100;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");

    public async Task<SeoPagePublicDto?> GetComplianceGuideAsync(
        string regionSlug,
        string comuneSlug,
        bool allowDraft,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.GetPublishedPageAsync(
            SeoPageType.ComplianceGuide,
            regionSlug,
            comuneSlug,
            allowDraft,
            cancellationToken);

        return page is null ? null : await MapPublicPageAsync(page, cancellationToken);
    }

    public async Task<SeoPagePublicDto?> GetTouristTaxPageAsync(
        string comuneSlug,
        bool allowDraft,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.GetPublishedTouristTaxPageAsync(comuneSlug, allowDraft, cancellationToken);
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

        var mapped = new List<SeoPageAdminDto>();
        foreach (var item in items)
        {
            mapped.Add(await MapAdminPageAsync(item, cancellationToken));
        }

        return (mapped, total);
    }

    public async Task<SeoPageAdminDto?> UpdateReviewStatusAsync(
        Guid pageId,
        LegalReviewStatus status,
        bool counselApproved,
        CancellationToken cancellationToken = default)
    {
        var page = await repository.GetByIdAsync(pageId, cancellationToken);
        if (page is null)
            return null;

        if (status == LegalReviewStatus.Reviewed && page.CounselRequired && !counselApproved)
        {
            throw new InvalidOperationException(
                "[COUNSEL_REQUIRED] First 100 SEO pages require counsel approval before publish.");
        }

        page.LegalReviewStatus = status;
        page.PublishedAt = status == LegalReviewStatus.Reviewed ? DateTime.UtcNow : page.PublishedAt;
        var updated = await repository.UpdatePageAsync(page, cancellationToken);
        return updated is null ? null : await MapAdminPageAsync(updated, cancellationToken);
    }

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

    public async Task<int> ApproveAllDraftPagesAsync(bool counselApproved, CancellationToken cancellationToken = default)
    {
        var (items, _) = await repository.ListPagesAsync(
            LegalReviewStatus.Draft,
            pageType: null,
            comuneCode: null,
            page: 1,
            pageSize: 500,
            cancellationToken);

        var approved = 0;
        foreach (var page in items)
        {
            try
            {
                var result = await UpdateReviewStatusAsync(
                    page.Id,
                    LegalReviewStatus.Reviewed,
                    counselApproved,
                    cancellationToken);
                if (result is not null)
                    approved++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not auto-approve SEO page {PageId}", page.Id);
            }
        }

        return approved;
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

    public async Task<string> BuildComplianceSitemapXmlAsync(CancellationToken cancellationToken = default)
    {
        var pages = await repository.GetReviewedPagesForSitemapAsync(cancellationToken);
        var today = _clock.TodayInRomeAsDateOnly();
        var baseUrl = configuration["Seo:PublicBaseUrl"] ?? "https://www.casazen.it";
        XNamespace ns = "http://www.sitemaps.org/schemas/sitemap/0.9";

        var urlset = new XElement(ns + "urlset");
        foreach (var page in pages)
        {
            var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode);
            if (comune is null)
                continue;

            var loc = page.PageType switch
            {
                SeoPageType.ComplianceGuide =>
                    $"{baseUrl}/p/affitti-brevi/{comune.RegionSlug}/{comune.ComuneSlug}",
                SeoPageType.TouristTaxCalc =>
                    $"{baseUrl}/p/tassa-soggiorno/{comune.ComuneSlug}",
                _ => null,
            };

            if (loc is null)
                continue;

            // A8-12: a calculator page of a comune without a rate in force is not worth indexing.
            if (page.PageType == SeoPageType.TouristTaxCalc
                && (await touristTaxQuoteService.GetRatesInForceAsync(ToTouristTaxComune(comune), today, cancellationToken)).Count == 0)
            {
                continue;
            }

            urlset.Add(new XElement(ns + "url",
                new XElement(ns + "loc", loc),
                new XElement(ns + "lastmod", (page.LastRefreshedAt ?? page.UpdatedAt).ToString("yyyy-MM-dd"))));
        }

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), urlset).ToString();
    }

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
        var existing = await repository.GetPublishedPageAsync(
            pageType,
            comune.RegionSlug,
            comune.ComuneSlug,
            allowDraft: true,
            cancellationToken);

        if (existing is not null && !forceRegenerate)
        {
            var latest = await repository.GetLatestRevisionAsync(existing.Id, cancellationToken);
            if (latest?.SourceDataVersion == sourceVersion)
            {
                logger.LogInformation("SEO source unchanged for {Slug}; skipping LLM", slug);
                return false;
            }
        }

        var cacheKey = $"{comune.Code}:{pageType}:{sourceVersion}";
        var prompt = BuildPrompt(comune, pageType, taxRates);
        // A paid provider is wrapped by the platform budget guard: it checks the cap BEFORE the call and throws
        // AiBudgetExceededException, which stops the batch (A8-07). The prompt holds public regulatory data only.
        var aiResult = await aiProvider.GenerateAsync(prompt, AiModelTier.Economy, cacheKey, cancellationToken);

        var page = existing ?? new SeoContentPage
        {
            Slug = slug,
            ComuneCode = comune.Code,
            RegionCode = comune.RegionCode,
            PageType = pageType,
            CounselRequired = ordinalHint < CounselRequiredBatchSize,
        };

        page.Slug = slug;
        page.Title = BuildTitle(comune, pageType);
        page.MetaDescription = BuildMetaDescription(comune, pageType);
        page.LastRefreshedAt = DateTime.UtcNow;
        page = await repository.UpsertPageAsync(page, cancellationToken);

        await repository.AddRevisionAsync(new SeoContentRevision
        {
            PageId = page.Id,
            BodyHtml = SeoHtmlSanitizer.Sanitize(aiResult.Content),
            AiModelTier = AiModelTier.Economy,
            PromptTokens = aiResult.FromCache ? 0 : aiResult.PromptTokens,
            GeneratedAt = DateTime.UtcNow,
            SourceDataVersion = sourceVersion,
        }, cancellationToken);

        return true;
    }

    private async Task<SeoPagePublicDto> MapPublicPageAsync(SeoContentPage page, CancellationToken cancellationToken)
    {
        var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode)
            ?? throw new InvalidOperationException($"Unknown comune code {page.ComuneCode}");

        var revision = await repository.GetLatestRevisionAsync(page.Id, cancellationToken);
        var bodyHtml = SeoHtmlSanitizer.Sanitize(revision?.BodyHtml);
        var refreshedAt = page.LastRefreshedAt ?? revision?.GeneratedAt;
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
            BuildCta(comune),
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

    private async Task<SeoPageAdminDto> MapAdminPageAsync(SeoContentPage page, CancellationToken cancellationToken)
    {
        var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode);
        var revision = await repository.GetLatestRevisionAsync(page.Id, cancellationToken);

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
            revision is null
                ? null
                : new SeoRevisionAdminDto(
                    revision.GeneratedAt,
                    revision.AiModelTier.ToString(),
                    revision.PromptTokens,
                    revision.SourceDataVersion));
    }

    /// <summary>
    /// Changes when a rate of the comune changes. With zero or one rate it keeps the pre-BK-03 format, so existing pages
    /// are not regenerated just because the format changed.
    /// </summary>
    private static string BuildSourceDataVersion(ComuneInfo comune, IReadOnlyList<TouristTaxRate> taxRates)
    {
        var first = taxRates.FirstOrDefault();
        var version = $"{comune.Code}:{first?.UpdatedAt:O}:{first?.RatePerPersonPerNight}:{first?.MaxNights}";
        foreach (var rate in taxRates.Skip(1))
            version += $"|{rate.Id:N}:{rate.UpdatedAt:O}";

        return version;
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

    private static string BuildPrompt(ComuneInfo comune, SeoPageType pageType, IReadOnlyList<TouristTaxRate> taxRates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Comune: {comune.Name}");
        sb.AppendLine($"PageType: {pageType}");
        sb.AppendLine("CIN: obbligatorio per affitti brevi in Italia.");
        sb.AppendLine("Alloggiati Web: comunicazione ospiti entro 24h dal check-in.");
        foreach (var taxRate in taxRates)
        {
            var amount = taxRate.CalculationMethod == TouristTaxCalculationMethod.PercentOfNightlyPrice
                ? $"{taxRate.PercentOfNightlyPrice}% del prezzo per notte, max €{taxRate.CapPerPersonPerNight?.ToString() ?? "none"}"
                : $"€{taxRate.RatePerPersonPerNight}/persona/notte";
            var category = taxRate.AccommodationCategory is null ? string.Empty : $" [{taxRate.AccommodationCategory}]";
            var season = taxRate.SeasonStart is null ? string.Empty : $" stagione {taxRate.SeasonStart}..{taxRate.SeasonEnd}";
            sb.AppendLine(
                $"TouristTaxRate{category}: {amount}, max nights {taxRate.MaxNights?.ToString() ?? "none"}, esenti sotto {taxRate.MinimumAge} anni{season}");
        }

        return sb.ToString();
    }

    private string BuildCanonicalUrl(ComuneInfo comune, SeoPageType pageType)
    {
        var baseUrl = configuration["Seo:PublicBaseUrl"] ?? "https://www.casazen.it";
        return pageType switch
        {
            SeoPageType.ComplianceGuide => $"{baseUrl}/p/affitti-brevi/{comune.RegionSlug}/{comune.ComuneSlug}",
            SeoPageType.TouristTaxCalc => $"{baseUrl}/p/tassa-soggiorno/{comune.ComuneSlug}",
            _ => $"{baseUrl}/p/supplier/{comune.ComuneSlug}",
        };
    }

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

    private static SeoCtaDto BuildCta(ComuneInfo comune) =>
        new(
            $"/tools/verifica-conformita?comune={comune.ComuneSlug}&utm_source=seo-compliance",
            "/signup?utm_source=seo-compliance&utm_medium=cta");

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
