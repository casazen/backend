using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Regulatory;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Repositories;

public class SeoContentRepository(AppDbContext context) : ISeoContentRepository
{
    public async Task<SeoContentPage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.SeoContentPages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<SeoContentPage?> GetPublishedPageAsync(
        SeoPageType pageType,
        string regionSlug,
        string comuneSlug,
        bool allowDraft,
        CancellationToken cancellationToken = default)
    {
        var comune = ItalianComuneRegistry.GetByRegionAndComuneSlug(regionSlug, comuneSlug);
        if (comune is null)
            return null;

        var query = context.SeoContentPages
            .AsNoTracking()
            .Where(p => p.PageType == pageType && p.ComuneCode == comune.Code);

        query = allowDraft
            ? query.Where(p => p.LegalReviewStatus == LegalReviewStatus.Reviewed ||
                               p.LegalReviewStatus == LegalReviewStatus.Draft)
            : query.Where(p => p.LegalReviewStatus == LegalReviewStatus.Reviewed);

        return await query.OrderByDescending(p => p.LastRefreshedAt).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<SeoContentPage?> GetPublishedTouristTaxPageAsync(
        string comuneSlug,
        bool allowDraft,
        CancellationToken cancellationToken = default)
    {
        var comune = ItalianComuneRegistry.GetBySlug(comuneSlug);
        if (comune is null)
            return null;

        var query = context.SeoContentPages
            .AsNoTracking()
            .Where(p => p.PageType == SeoPageType.TouristTaxCalc && p.ComuneCode == comune.Code);

        query = allowDraft
            ? query.Where(p => p.LegalReviewStatus == LegalReviewStatus.Reviewed ||
                               p.LegalReviewStatus == LegalReviewStatus.Draft)
            : query.Where(p => p.LegalReviewStatus == LegalReviewStatus.Reviewed);

        return await query.OrderByDescending(p => p.LastRefreshedAt).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<SeoContentPage> Items, int TotalCount)> ListPagesAsync(
        LegalReviewStatus? legalReviewStatus,
        SeoPageType? pageType,
        string? comuneCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = context.SeoContentPages.AsNoTracking();

        if (legalReviewStatus.HasValue)
            query = query.Where(p => p.LegalReviewStatus == legalReviewStatus.Value);

        if (pageType.HasValue)
            query = query.Where(p => p.PageType == pageType.Value);

        if (!string.IsNullOrWhiteSpace(comuneCode))
            query = query.Where(p => p.ComuneCode == comuneCode);

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    public async Task<IReadOnlyList<SeoContentPage>> GetReviewedPagesForSitemapAsync(
        CancellationToken cancellationToken = default)
    {
        return await context.SeoContentPages
            .AsNoTracking()
            .Where(p => p.LegalReviewStatus == LegalReviewStatus.Reviewed &&
                        (p.PageType == SeoPageType.ComplianceGuide || p.PageType == SeoPageType.TouristTaxCalc))
            .OrderBy(p => p.Slug)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SeoContentPage>> GetPagesNeedingRefreshAsync(
        CancellationToken cancellationToken = default)
    {
        var pages = await context.SeoContentPages
            .AsNoTracking()
            .Where(p => p.PageType == SeoPageType.ComplianceGuide || p.PageType == SeoPageType.TouristTaxCalc)
            .ToListAsync(cancellationToken);

        if (pages.Count == 0)
            return pages;

        var taxRates = await context.TouristTaxRates
            .AsNoTracking()
            .Where(t => t.IsActive)
            .ToListAsync(cancellationToken);

        var stale = new List<SeoContentPage>();
        foreach (var page in pages)
        {
            var comune = ItalianComuneRegistry.GetByCode(page.ComuneCode);
            if (comune is null)
                continue;

            var rate = taxRates
                .Where(t => t.City.Equals(comune.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(t => t.UpdatedAt)
                .FirstOrDefault();

            if (rate is null)
                continue;

            if (!page.LastRefreshedAt.HasValue || rate.UpdatedAt > page.LastRefreshedAt.Value)
                stale.Add(page);
        }

        return stale;
    }

    public async Task<int> CountAllPagesAsync(CancellationToken cancellationToken = default)
    {
        return await context.SeoContentPages.CountAsync(cancellationToken);
    }

    public async Task<SeoContentPage> UpsertPageAsync(SeoContentPage page, CancellationToken cancellationToken = default)
    {
        page.UpdatedAt = DateTime.UtcNow;
        var existing = await context.SeoContentPages
            .FirstOrDefaultAsync(
                p => p.ComuneCode == page.ComuneCode && p.PageType == page.PageType,
                cancellationToken);

        if (existing is null)
        {
            context.SeoContentPages.Add(page);
            await context.SaveChangesAsync(cancellationToken);
            return page;
        }

        existing.Slug = page.Slug;
        existing.RegionCode = page.RegionCode;
        existing.Title = page.Title;
        existing.MetaDescription = page.MetaDescription;
        existing.LastRefreshedAt = page.LastRefreshedAt;
        existing.CounselRequired = page.CounselRequired;
        existing.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<SeoContentPage?> UpdatePageAsync(SeoContentPage page, CancellationToken cancellationToken = default)
    {
        var existing = await context.SeoContentPages.FirstOrDefaultAsync(p => p.Id == page.Id, cancellationToken);
        if (existing is null)
            return null;

        existing.LegalReviewStatus = page.LegalReviewStatus;
        existing.PublishedAt = page.PublishedAt;
        existing.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task AddRevisionAsync(SeoContentRevision revision, CancellationToken cancellationToken = default)
    {
        context.SeoContentRevisions.Add(revision);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<SeoContentRevision?> GetLatestRevisionAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
    {
        return await context.SeoContentRevisions
            .AsNoTracking()
            .Where(r => r.PageId == pageId)
            .OrderByDescending(r => r.GeneratedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<PlatformAiBudget> GetOrCreatePlatformAiBudgetAsync(CancellationToken cancellationToken = default)
    {
        var budget = await context.PlatformAiBudgets.FirstOrDefaultAsync(cancellationToken);
        if (budget is not null)
            return budget;

        budget = new PlatformAiBudget();
        context.PlatformAiBudgets.Add(budget);
        await context.SaveChangesAsync(cancellationToken);
        return budget;
    }

    public async Task SavePlatformAiBudgetAsync(PlatformAiBudget budget, CancellationToken cancellationToken = default)
    {
        budget.UpdatedAt = DateTime.UtcNow;
        context.PlatformAiBudgets.Update(budget);
        await context.SaveChangesAsync(cancellationToken);
    }
}
