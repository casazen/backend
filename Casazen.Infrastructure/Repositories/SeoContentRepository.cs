using System.Data;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Regulatory;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Casazen.Infrastructure.Repositories;

public class SeoContentRepository(AppDbContext context) : ISeoContentRepository
{
    public async Task<SeoContentPage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.SeoContentPages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    public async Task<SeoContentPage?> GetPageAsync(
        string comuneCode,
        SeoPageType pageType,
        CancellationToken cancellationToken = default)
    {
        return await context.SeoContentPages
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.ComuneCode == comuneCode && p.PageType == pageType, cancellationToken);
    }

    public async Task<SeoContentPage?> GetPublishedPageAsync(
        SeoPageType pageType,
        string regionSlug,
        string comuneSlug,
        CancellationToken cancellationToken = default)
    {
        var comune = ItalianComuneRegistry.GetByRegionAndComuneSlug(regionSlug, comuneSlug);
        return comune is null ? null : await GetPublishedAsync(pageType, comune.Code, cancellationToken);
    }

    public async Task<SeoContentPage?> GetPublishedTouristTaxPageAsync(
        string comuneSlug,
        CancellationToken cancellationToken = default)
    {
        var comune = ItalianComuneRegistry.GetBySlug(comuneSlug);
        return comune is null ? null : await GetPublishedAsync(SeoPageType.TouristTaxCalc, comune.Code, cancellationToken);
    }

    /// <summary>SE-01 (A8-05): the public sees the approved revision only, never the latest one.</summary>
    private async Task<SeoContentPage?> GetPublishedAsync(
        SeoPageType pageType,
        string comuneCode,
        CancellationToken cancellationToken)
    {
        return await context.SeoContentPages
            .AsNoTracking()
            .Include(p => p.PublishedRevision)
            .Where(p => p.PageType == pageType && p.ComuneCode == comuneCode && p.PublishedRevisionId != null)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<(IReadOnlyList<SeoPageListItem> Items, int TotalCount)> ListPagesAsync(
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
        var rows = await query
            .OrderByDescending(p => p.UpdatedAt)
            .ThenBy(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                Page = p,
                Latest = p.Revisions
                    .OrderByDescending(r => r.GeneratedAt)
                    .ThenByDescending(r => r.Id)
                    .Select(r => new
                    {
                        r.Id,
                        r.GeneratedAt,
                        r.AiModelTier,
                        r.PromptTokens,
                        r.SourceDataVersion,
                        r.ContentStatus,
                        r.PromptVersion,
                    })
                    .FirstOrDefault(),
                LastEvent = context.SeoContentReviewEvents
                    .Where(e => e.PageId == p.Id)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => new { e.Action, e.RevisionId, e.ActorUserId, e.OccurredAt, e.CounselApproved, e.Note })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(row => new SeoPageListItem(
                row.Page,
                row.Latest is null
                    ? null
                    : new SeoRevisionSummary(
                        row.Latest.Id,
                        row.Latest.GeneratedAt,
                        row.Latest.AiModelTier,
                        row.Latest.PromptTokens,
                        row.Latest.SourceDataVersion,
                        row.Latest.ContentStatus,
                        row.Latest.PromptVersion),
                row.LastEvent is null
                    ? null
                    : new SeoReviewEventSummary(
                        row.LastEvent.Action,
                        row.LastEvent.RevisionId,
                        row.LastEvent.ActorUserId,
                        row.LastEvent.OccurredAt,
                        row.LastEvent.CounselApproved,
                        row.LastEvent.Note)))
            .ToList();

        return (items, total);
    }

    public async Task<IReadOnlyList<SeoContentPage>> GetPublishedPagesForSitemapAsync(
        CancellationToken cancellationToken = default)
    {
        // SE-02 (A8-02) + SE-01: only published pages, with the text of their approved revision. Bodies are sanitized and
        // trimmed when stored (FD-15) and an approval requires a body, so the check is only a safety net.
        return await context.SeoContentPages
            .AsNoTracking()
            .Include(p => p.PublishedRevision)
            .Where(p => p.PublishedRevisionId != null &&
                        p.PublishedRevision!.BodyHtml != "" &&
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

            // Same comune matching as the tourist tax engine (ISTAT code, else normalized name: A8-23).
            var touristTaxComune = new TouristTaxComune(comune.Code, comune.Name);
            var rate = taxRates
                .Where(touristTaxComune.Matches)
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

    public async Task AddRevisionAsync(SeoContentRevision revision, CancellationToken cancellationToken = default)
    {
        // Every stored revision goes through the allowlist, whoever writes it (AI generation or manual edit).
        revision.BodyHtml = SeoHtmlSanitizer.Sanitize(revision.BodyHtml);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        // Under the page lock an approval cannot run between the new text and the Draft state (A8-05).
        await LockPageAsync(revision.PageId, cancellationToken);
        var page = await LoadPageForUpdateAsync(revision.PageId, cancellationToken)
            ?? throw new InvalidOperationException($"SEO page {revision.PageId} not found");

        context.SeoContentRevisions.Add(revision);
        page.LegalReviewStatus = LegalReviewStatus.Draft;
        page.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
    }

    public async Task<SeoContentRevision?> GetLatestRevisionAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
    {
        return await context.SeoContentRevisions
            .AsNoTracking()
            .Where(r => r.PageId == pageId)
            .OrderByDescending(r => r.GeneratedAt)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<SeoContentRevision?> GetRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default)
    {
        return await context.SeoContentRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == revisionId, cancellationToken);
    }

    public async Task<IReadOnlyList<SeoReviewEventSummary>> GetReviewEventsAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
    {
        return await context.SeoContentReviewEvents
            .AsNoTracking()
            .Where(e => e.PageId == pageId)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new SeoReviewEventSummary(e.Action, e.RevisionId, e.ActorUserId, e.OccurredAt, e.CounselApproved, e.Note))
            .ToListAsync(cancellationToken);
    }

    public async Task<SeoApprovalOutcome> ApproveRevisionAsync(
        SeoContentReviewEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockPageAsync(auditEvent.PageId, cancellationToken);
        var page = await LoadPageForUpdateAsync(auditEvent.PageId, cancellationToken);
        if (page is null)
            return SeoApprovalOutcome.PageNotFound;

        var revision = auditEvent.RevisionId is { } revisionId
            ? await context.SeoContentRevisions
                .AsNoTracking()
                .Where(r => r.Id == revisionId && r.PageId == page.Id)
                .Select(r => new { r.Id, r.ContentStatus, HasBody = r.BodyHtml != "" })
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (revision is null)
            return SeoApprovalOutcome.RevisionNotFound;

        // The admin approves the text they read: a newer revision (generated after they opened it) is never approved blind.
        var latest = await GetLatestRevisionAsync(page.Id, cancellationToken);
        if (latest?.Id != revision.Id)
            return SeoApprovalOutcome.RevisionOutdated;

        if (page.PublishedRevisionId == revision.Id)
            return SeoApprovalOutcome.AlreadyPublished;

        if (revision.ContentStatus != SeoContentStatus.Generated || !revision.HasBody)
            return SeoApprovalOutcome.NotPublishable;

        auditEvent.Action = SeoReviewAction.Approved;
        page.PublishedRevisionId = revision.Id;
        page.LegalReviewStatus = LegalReviewStatus.Reviewed;
        page.PublishedAt = auditEvent.OccurredAt;
        page.UpdatedAt = auditEvent.OccurredAt;
        context.SeoContentReviewEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return SeoApprovalOutcome.Approved;
    }

    public async Task<SeoWithdrawOutcome> WithdrawAsync(
        SeoContentReviewEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockPageAsync(auditEvent.PageId, cancellationToken);
        var page = await LoadPageForUpdateAsync(auditEvent.PageId, cancellationToken);
        if (page is null)
            return SeoWithdrawOutcome.PageNotFound;

        if (page.PublishedRevisionId is null)
            return SeoWithdrawOutcome.NotPublished;

        auditEvent.Action = SeoReviewAction.Withdrawn;
        auditEvent.RevisionId = page.PublishedRevisionId;
        page.PublishedRevisionId = null;
        page.PublishedAt = null;
        page.LegalReviewStatus = LegalReviewStatus.Draft;
        page.UpdatedAt = auditEvent.OccurredAt;
        context.SeoContentReviewEvents.Add(auditEvent);
        await context.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return SeoWithdrawOutcome.Withdrawn;
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (!context.Database.IsRelational() || context.Database.CurrentTransaction is not null)
            return null;

        return await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
    }

    /// <summary>Row lock on the page until the end of the transaction: new revisions, approvals and withdrawals of a page run one after the other.</summary>
    private async Task LockPageAsync(Guid pageId, CancellationToken cancellationToken)
    {
        if (!context.Database.IsNpgsql())
            return;

        await context.Database
            .SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM "SeoContentPages" WHERE "Id" = {pageId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
    }

    /// <summary>The page as stored now: a tracked instance is refreshed, its values may predate the lock.</summary>
    private async Task<SeoContentPage?> LoadPageForUpdateAsync(Guid pageId, CancellationToken cancellationToken)
    {
        var page = await context.SeoContentPages.FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is not null)
            await context.Entry(page).ReloadAsync(cancellationToken);
        return page;
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
