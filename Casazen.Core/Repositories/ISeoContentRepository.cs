using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Repositories;

/// <summary>A revision without its text, for the admin list.</summary>
public sealed record SeoRevisionSummary(
    Guid Id,
    DateTime GeneratedAt,
    AiModelTier AiModelTier,
    int PromptTokens,
    string SourceDataVersion,
    SeoContentStatus ContentStatus,
    string? PromptVersion);

/// <summary>An entry of the review audit of a page.</summary>
public sealed record SeoReviewEventSummary(
    SeoReviewAction Action,
    Guid? RevisionId,
    string ActorUserId,
    DateTime OccurredAt,
    bool CounselApproved,
    string? Note);

/// <summary>A page of the admin list: its latest revision (the one waiting for review when not published) and last review action.</summary>
public sealed record SeoPageListItem(SeoContentPage Page, SeoRevisionSummary? LatestRevision, SeoReviewEventSummary? LastReviewEvent);

/// <summary>Outcome of <see cref="ISeoContentRepository.ApproveRevisionAsync"/>.</summary>
public enum SeoApprovalOutcome
{
    Approved,
    PageNotFound,
    RevisionNotFound,

    /// <summary>The revision is not the latest one any more: a newer text arrived after the admin opened it.</summary>
    RevisionOutdated,

    /// <summary>The revision is already the published one.</summary>
    AlreadyPublished,

    /// <summary>The revision holds no publishable text (<see cref="SeoContentStatus"/> other than Generated, or empty).</summary>
    NotPublishable,
}

/// <summary>Outcome of <see cref="ISeoContentRepository.WithdrawAsync"/>.</summary>
public enum SeoWithdrawOutcome
{
    Withdrawn,
    PageNotFound,
    NotPublished,
}

public interface ISeoContentRepository
{
    Task<SeoContentPage?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The page of a comune and type, published or not (generation and admin).</summary>
    Task<SeoContentPage?> GetPageAsync(string comuneCode, SeoPageType pageType, CancellationToken cancellationToken = default);

    /// <summary>
    /// The public compliance guide of a comune with its <see cref="SeoContentPage.PublishedRevision"/> loaded; <c>null</c>
    /// when the page does not exist or has no approved revision (SE-01: never a draft, in any environment).
    /// </summary>
    Task<SeoContentPage?> GetPublishedPageAsync(
        SeoPageType pageType, string regionSlug, string comuneSlug, CancellationToken cancellationToken = default);

    /// <summary>The public tourist tax page of a comune, same rules as <see cref="GetPublishedPageAsync"/>.</summary>
    Task<SeoContentPage?> GetPublishedTouristTaxPageAsync(string comuneSlug, CancellationToken cancellationToken = default);

    /// <summary>
    /// Admin list, newest first. <paramref name="legalReviewStatus"/> <c>Draft</c> lists the pages whose latest revision
    /// waits for a review (published or not), <c>Reviewed</c> those whose latest revision is the published one.
    /// </summary>
    Task<(IReadOnlyList<SeoPageListItem> Items, int TotalCount)> ListPagesAsync(
        LegalReviewStatus? legalReviewStatus, SeoPageType? pageType, string? comuneCode,
        int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Published compliance guides and tourist tax pages whose approved revision has a body, ordered by slug, with
    /// <see cref="SeoContentPage.PublishedRevision"/> loaded: the candidates of the sitemap and of the public hub (the
    /// service drops calculators without a rate).
    /// </summary>
    Task<IReadOnlyList<SeoContentPage>> GetPublishedPagesForSitemapAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SeoContentPage>> GetPagesNeedingRefreshAsync(CancellationToken cancellationToken = default);

    Task<int> CountAllPagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the page or updates its title, description, slug and refresh date. Never changes the review state nor the
    /// published revision.
    /// </summary>
    Task<SeoContentPage> UpsertPageAsync(SeoContentPage page, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a new revision (body sanitized with the FD-15 allowlist) and puts the page back to <c>Draft</c> in the same
    /// transaction: a new text, generated or edited, always waits for a review. The published revision is not changed.
    /// </summary>
    Task AddRevisionAsync(SeoContentRevision revision, CancellationToken cancellationToken = default);

    Task<SeoContentRevision?> GetLatestRevisionAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<SeoContentRevision?> GetRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default);

    /// <summary>The review audit of a page, newest first.</summary>
    Task<IReadOnlyList<SeoReviewEventSummary>> GetReviewEventsAsync(Guid pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes <paramref name="auditEvent"/>'s revision: under a row lock on the page it must still be the latest,
    /// publishable revision of the page; then the page gets it as published revision, <c>Reviewed</c>, and the audit row
    /// is written, all in one transaction.
    /// </summary>
    Task<SeoApprovalOutcome> ApproveRevisionAsync(SeoContentReviewEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws the published revision of <paramref name="auditEvent"/>'s page (back to <c>Draft</c>, not public) and
    /// writes the audit row (with the revision that was published), in one transaction under a row lock on the page.
    /// </summary>
    Task<SeoWithdrawOutcome> WithdrawAsync(SeoContentReviewEvent auditEvent, CancellationToken cancellationToken = default);

    Task<PlatformAiBudget> GetOrCreatePlatformAiBudgetAsync(CancellationToken cancellationToken = default);

    Task SavePlatformAiBudgetAsync(PlatformAiBudget budget, CancellationToken cancellationToken = default);
}
