using Casazen.Core.Entities.Enums;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

public record SeoDisclaimersDto(string LastUpdated, string NotLegalAdvice, string AiGenerated);
/// <summary>
/// Call to action of a public SEO page (SE-03): <c>SignupUrl</c> is the web app route <c>/signup</c> with the comune and
/// the default UTM parameters, on <c>App:PublicSiteBaseUrl</c> (relative only when that is not configured, in
/// Development/Testing). There is no compliance checker tool: its CTA was removed until a spec exists.
/// </summary>
public record SeoCtaDto(string SignupUrl);
/// <summary>A tourist tax rate of the comune in force today, as shown on the public page (one per category or season).</summary>
public record PublicTouristTaxRateSummaryDto(
    string City,
    string? AccommodationCategory,
    string? SeasonStart,
    string? SeasonEnd,
    TouristTaxCalculationMethod CalculationMethod,
    decimal RatePerPersonPerNight,
    decimal? PercentOfNightlyPrice,
    decimal? CapPerPersonPerNight,
    int? MaxNights,
    int MinimumAge,
    int? ReducedRateMaxAge,
    decimal? ReducedRatePerPersonPerNight,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    string? SourceUrl);

public record SeoPagePublicDto(
    Guid Id, SeoPageType PageType, string Title, string MetaDescription, string BodyHtml,
    string ComuneName, string ComuneCode, string RegionCode, string RegionSlug, string ComuneSlug,
    string? CanonicalUrl, DateTime? LastRefreshedAt, SeoDisclaimersDto Disclaimers, SeoCtaDto Cta,
    IReadOnlyList<PublicTouristTaxRateSummaryDto> TouristTaxRates);

/// <summary>A published SEO page listed by the public hub; <c>Path</c> is the web app route (<c>/p/…</c>).</summary>
public record SeoPublishedPageDto(
    SeoPageType PageType, string Title, string ComuneName, string RegionSlug, string ComuneSlug, string Path);

/// <summary>
/// The public hub (<c>/p/affitti-brevi</c>): the pages the sitemap lists, and the hub canonical URL (<c>null</c> only
/// when <c>App:PublicSiteBaseUrl</c> is not configured, in Development/Testing).
/// </summary>
public record SeoPublishedPagesDto(string? CanonicalUrl, IReadOnlyList<SeoPublishedPageDto> Pages);

/// <summary>Public tourist tax calculator (A8-12, A8-23): same engine as the checkout.</summary>
/// <param name="ChildrenAges">Age of each minor at check-in (0-17), when the comune exempts or reduces minors by age.</param>
/// <param name="AccommodationCategory">Category of the accommodation, for comuni whose rates depend on it.</param>
/// <param name="NightlyPrice">Price of one night of the accommodation, for percentage rates.</param>
public record PublicTouristTaxCalculateRequest(
    string ComuneSlug, int NumberOfAdults, int NumberOfChildren,
    DateTime CheckInDate, DateTime CheckOutDate,
    IReadOnlyList<int>? ChildrenAges = null,
    string? AccommodationCategory = null,
    decimal? NightlyPrice = null);

/// <param name="Status">
/// <c>Calculated</c>: <paramref name="TaxAmount"/> is set. <c>RateUnavailable</c>: CasaZen has no rate for the comune
/// or the dates. Otherwise the input the calculation still needs (category, ages of the minors, night price).
/// </param>
public record PublicTouristTaxCalculateResponse(
    string ComuneSlug, string City, TouristTaxQuoteStatus Status, decimal? TaxAmount,
    int NumberOfAdults, int NumberOfChildren, int Nights, int TaxableNights, bool AgeRulesApply,
    IReadOnlyList<string> Categories, DateTime CheckInDate, DateTime CheckOutDate);

/// <summary>A revision in the admin list (without its text). <c>ContentStatus</c> other than Generated: nothing to publish.</summary>
public record SeoRevisionAdminDto(
    Guid Id, DateTime GeneratedAt, string AiModelTier, int PromptTokens, string SourceDataVersion,
    SeoContentStatus ContentStatus, string? PromptVersion);

/// <summary>An entry of the review audit: who approved or withdrew which revision, when, with which note.</summary>
public record SeoReviewEventDto(
    SeoReviewAction Action, Guid? RevisionId, string ActorUserId, DateTime OccurredAt, bool CounselApproved, string? Note);

/// <summary>
/// A page in the admin list (SE-01, A8-21). <c>IsPublished</c>: the public sees <c>PublishedRevisionId</c>.
/// <c>HasPendingRevision</c>: the latest revision (<c>LatestRevision</c>) is not the published one and waits for a review.
/// <c>PublicPath</c> / <c>PublicUrl</c>: the public page (<c>PublicUrl</c> on <c>App:PublicSiteBaseUrl</c>, null when not
/// configured; <c>PublicPath</c> null for a comune missing from the registry).
/// </summary>
public record SeoPageAdminDto(
    Guid Id, string Slug, string ComuneCode, string ComuneName, string RegionCode, string RegionSlug,
    SeoPageType PageType, string Title, LegalReviewStatus LegalReviewStatus,
    DateTime? PublishedAt, DateTime? LastRefreshedAt, SeoRevisionAdminDto? LatestRevision,
    Guid? PublishedRevisionId, bool IsPublished, bool HasPendingRevision, bool CounselRequired,
    string? PublicPath, string? PublicUrl, SeoReviewEventDto? LastReviewEvent);

/// <summary>A revision with its text, sanitized with the FD-15 allowlist, for the admin preview.</summary>
public record SeoRevisionPreviewDto(
    Guid Id, DateTime GeneratedAt, SeoContentStatus ContentStatus, string? PromptVersion, string SourceDataVersion,
    string BodyHtml);

/// <summary>
/// Review screen of a page: the published text and the one waiting for a review (null when there is none: the latest
/// revision is the published one), plus the whole review audit, newest first.
/// </summary>
public record SeoPageAdminDetailDto(
    SeoPageAdminDto Page, SeoRevisionPreviewDto? PublishedRevision, SeoRevisionPreviewDto? PendingRevision,
    IReadOnlyList<SeoReviewEventDto> ReviewHistory);

/// <summary>Approval of <paramref name="RevisionId"/>, the revision the admin read, by <paramref name="ActorUserId"/>.</summary>
public record SeoApproveRevisionCommand(Guid PageId, Guid RevisionId, bool CounselApproved, string? Note, string ActorUserId);

/// <summary>Withdrawal of the published revision of a page by <paramref name="ActorUserId"/>.</summary>
public record SeoWithdrawCommand(Guid PageId, string? Note, string ActorUserId);

/// <summary>
/// Queues the AI generation. Every generated text is a draft that waits for a review: there is no automatic approval
/// (SE-01, A8-04).
/// </summary>
public record SeoGenerateRequestDto(
    IReadOnlyList<string> ComuneCodes,
    IReadOnlyList<SeoPageType>? PageTypes,
    bool ForceRegenerate);
public record SeoGenerateAcceptedDto(string JobId, DateTime EnqueuedAt, int ComuneCount, int EstimatedPages);
public record PlatformAiBudgetDto(long MonthlyTokenCap, long TokensUsedThisMonth, DateTime LastResetAt);

/// <summary>Stable error codes of the SEO review (SE-01), with their <c>SharedResources</c> keys.</summary>
public static class SeoReviewErrorCodes
{
    /// <summary>422: a page among the first 100 needs the confirmation of the legal review.</summary>
    public const string CounselRequired = "seo_counsel_required";

    /// <summary>422: the revision holds no publishable text (not generated, empty or invalid).</summary>
    public const string RevisionNotPublishable = "seo_revision_not_publishable";

    /// <summary>409: a newer revision arrived after the admin opened this one.</summary>
    public const string RevisionOutdated = "seo_revision_outdated";

    /// <summary>409: the revision is already the published one.</summary>
    public const string RevisionAlreadyPublished = "seo_revision_already_published";

    /// <summary>409: the page has no published revision to withdraw.</summary>
    public const string PageNotPublished = "seo_page_not_published";
}

public interface ISeoContentService
{
    /// <summary>The public guide with its approved revision; <c>null</c> when the page has none (SE-01: in every environment).</summary>
    Task<SeoPagePublicDto?> GetComplianceGuideAsync(string regionSlug, string comuneSlug, CancellationToken cancellationToken = default);
    Task<SeoPagePublicDto?> GetTouristTaxPageAsync(string comuneSlug, CancellationToken cancellationToken = default);
    Task<PublicTouristTaxCalculateResponse?> CalculateTouristTaxAsync(PublicTouristTaxCalculateRequest request, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<SeoPageAdminDto> Items, int TotalCount)> ListPagesAsync(LegalReviewStatus? legalReviewStatus, SeoPageType? pageType, string? comuneCode, int page, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Review screen of a page (published and pending text, audit); <c>null</c> when the page does not exist.</summary>
    Task<SeoPageAdminDetailDto?> GetAdminPageAsync(Guid pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the revision the admin read and records the audit. Throws <c>NotFoundException</c> (page or revision),
    /// <c>DomainRuleException</c> (<see cref="SeoReviewErrorCodes.CounselRequired"/>,
    /// <see cref="SeoReviewErrorCodes.RevisionNotPublishable"/>) or <c>DomainConflictException</c>
    /// (<see cref="SeoReviewErrorCodes.RevisionOutdated"/>, <see cref="SeoReviewErrorCodes.RevisionAlreadyPublished"/>).
    /// </summary>
    Task<SeoPageAdminDto> ApproveRevisionAsync(SeoApproveRevisionCommand command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws the published revision: the page goes back to draft, leaves the public site and the sitemap, and the
    /// audit is recorded. Throws <c>NotFoundException</c> or <c>DomainConflictException</c>
    /// (<see cref="SeoReviewErrorCodes.PageNotPublished"/>).
    /// </summary>
    Task<SeoPageAdminDto> WithdrawAsync(SeoWithdrawCommand command, CancellationToken cancellationToken = default);

    Task<PlatformAiBudgetDto> GetPlatformAiBudgetAsync(CancellationToken cancellationToken = default);

    /// <summary>Generates a new draft revision per page (never approved automatically); returns the revisions stored.</summary>
    Task<int> GeneratePagesForComuneBatchAsync(IReadOnlyList<string> comuneCodes, IReadOnlyList<SeoPageType> pageTypes, bool forceRegenerate, CancellationToken cancellationToken = default);
    Task<int> RefreshStalePagesAsync(CancellationToken cancellationToken = default);
    /// <summary>Pages worth indexing: published, with content, calculators only with a tourist tax rate in force.</summary>
    Task<SeoPublishedPagesDto> GetPublishedPagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sitemap of <see cref="GetPublishedPagesAsync"/> plus the hub, with absolute URLs on <c>App:PublicSiteBaseUrl</c>.
    /// Throws when the public URL is not configured (never a fallback domain).
    /// </summary>
    Task<string> BuildComplianceSitemapXmlAsync(CancellationToken cancellationToken = default);
}
