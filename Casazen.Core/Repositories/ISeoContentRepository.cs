using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public interface ISeoContentRepository
{
    Task<SeoContentPage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SeoContentPage?> GetPublishedPageAsync(
        SeoPageType pageType, string regionSlug, string comuneSlug, bool allowDraft,
        CancellationToken cancellationToken = default);

    Task<SeoContentPage?> GetPublishedTouristTaxPageAsync(
        string comuneSlug, bool allowDraft, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SeoContentPage> Items, int TotalCount)> ListPagesAsync(
        LegalReviewStatus? legalReviewStatus, SeoPageType? pageType, string? comuneCode,
        int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoContentPage>> GetReviewedPagesForSitemapAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoContentPage>> GetPagesNeedingRefreshAsync(CancellationToken cancellationToken = default);

    Task<int> CountAllPagesAsync(CancellationToken cancellationToken = default);

    Task<SeoContentPage> UpsertPageAsync(SeoContentPage page, CancellationToken cancellationToken = default);

    Task<SeoContentPage?> UpdatePageAsync(SeoContentPage page, CancellationToken cancellationToken = default);

    Task AddRevisionAsync(SeoContentRevision revision, CancellationToken cancellationToken = default);

    Task<SeoContentRevision?> GetLatestRevisionAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<PlatformAiBudget> GetOrCreatePlatformAiBudgetAsync(CancellationToken cancellationToken = default);

    Task SavePlatformAiBudgetAsync(PlatformAiBudget budget, CancellationToken cancellationToken = default);
}
