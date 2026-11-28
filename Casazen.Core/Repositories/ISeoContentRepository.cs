using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

public sealed record SeoRevisionSummary(
    Guid Id,
    DateTime GeneratedAt,
    AiModelTier AiModelTier,
    int PromptTokens,
    string SourceDataVersion,
    SeoContentStatus ContentStatus,
    string? PromptVersion);

public sealed record SeoReviewEventSummary(
    SeoReviewAction Action,
    Guid? RevisionId,
    string ActorUserId,
    DateTime OccurredAt,
    bool CounselApproved,
    string? Note);

public sealed record SeoPageListItem(SeoContentPage Page, SeoRevisionSummary? LatestRevision, SeoReviewEventSummary? LastReviewEvent);

public enum SeoApprovalOutcome
{
    Approved,
    PageNotFound,
    RevisionNotFound,

    RevisionOutdated,

    AlreadyPublished,

    NotPublishable,
}

public enum SeoWithdrawOutcome
{
    Withdrawn,
    PageNotFound,
    NotPublished,
}

public interface ISeoContentRepository
{
    Task<SeoContentPage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SeoContentPage?> GetPageAsync(string comuneCode, SeoPageType pageType, CancellationToken cancellationToken = default);

    Task<SeoContentPage?> GetPublishedPageAsync(
        SeoPageType pageType, string regionSlug, string comuneSlug, CancellationToken cancellationToken = default);

    Task<SeoContentPage?> GetPublishedTouristTaxPageAsync(string comuneSlug, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<SeoPageListItem> Items, int TotalCount)> ListPagesAsync(
        LegalReviewStatus? legalReviewStatus, SeoPageType? pageType, string? comuneCode,
        int page, int pageSize, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoContentPage>> GetPublishedPagesForSitemapAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoContentPage>> GetPagesNeedingRefreshAsync(CancellationToken cancellationToken = default);

    Task<int> CountAllPagesAsync(CancellationToken cancellationToken = default);

    Task<SeoContentPage> UpsertPageAsync(SeoContentPage page, CancellationToken cancellationToken = default);

    Task AddRevisionAsync(SeoContentRevision revision, CancellationToken cancellationToken = default);

    Task<SeoContentRevision?> GetLatestRevisionAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<SeoContentRevision?> GetRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoReviewEventSummary>> GetReviewEventsAsync(Guid pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes <paramref name="auditEvent"/>'s revision: under a row lock on the page it must still be the latest,
    /// publishable revision of the page; then the page gets it as published revision, <c>Reviewed</c>, and the audit row
    /// is written, all in one transaction.
    /// </summary>
    Task<SeoApprovalOutcome> ApproveRevisionAsync(SeoContentReviewEvent auditEvent, CancellationToken cancellationToken = default);

    Task<SeoWithdrawOutcome> WithdrawAsync(SeoContentReviewEvent auditEvent, CancellationToken cancellationToken = default);

    Task<PlatformAiBudget> GetOrCreatePlatformAiBudgetAsync(CancellationToken cancellationToken = default);

    Task SavePlatformAiBudgetAsync(PlatformAiBudget budget, CancellationToken cancellationToken = default);
}
