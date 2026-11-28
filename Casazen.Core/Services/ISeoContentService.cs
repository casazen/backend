using Casazen.Core.Entities.Enums;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

public record SeoCtaDto(string SignupUrl);
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

/// <summary>
/// A published SEO page. <c>BodyHtml</c>, <c>Title</c> and <c>MetaDescription</c> are written in <c>ContentLanguage</c>
/// (<c>it</c>: the regulations are Italian), whatever the language of the visitor: the client marks the text with it
/// (SE-05, A8-19). <c>AiGenerated</c>: the text was written by an AI model (then reviewed by an admin, SE-01): the client
/// shows the AI Act transparency notice next to it (A8-27). The last update is <c>LastRefreshedAt</c>; the disclaimers
/// ("not legal advice", "AI generated") are texts of the client, in the language of the visitor, not of this API.
/// <c>Cta</c> is <c>null</c> while the flag <c>SeoSignupCta</c> is off (private invitation, SEO-INVITE).
/// </summary>
public record SeoPagePublicDto(
    Guid Id, SeoPageType PageType, string Title, string MetaDescription, string BodyHtml,
    string ComuneName, string ComuneCode, string RegionCode, string RegionSlug, string ComuneSlug,
    string? CanonicalUrl, DateTime? LastRefreshedAt, bool AiGenerated, string ContentLanguage, SeoCtaDto? Cta,
    IReadOnlyList<PublicTouristTaxRateSummaryDto> TouristTaxRates);

public record SeoPublishedPageDto(
    SeoPageType PageType, string Title, string ComuneName, string RegionSlug, string ComuneSlug, string Path);

public record SeoPublishedPagesDto(string? CanonicalUrl, IReadOnlyList<SeoPublishedPageDto> Pages);

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

public record SeoRevisionAdminDto(
    Guid Id, DateTime GeneratedAt, string AiModelTier, int PromptTokens, string SourceDataVersion,
    SeoContentStatus ContentStatus, string? PromptVersion);

public record SeoReviewEventDto(
    SeoReviewAction Action, Guid? RevisionId, string ActorUserId, DateTime OccurredAt, bool CounselApproved, string? Note);

public record SeoPageAdminDto(
    Guid Id, string Slug, string ComuneCode, string ComuneName, string RegionCode, string RegionSlug,
    SeoPageType PageType, string Title, LegalReviewStatus LegalReviewStatus,
    DateTime? PublishedAt, DateTime? LastRefreshedAt, SeoRevisionAdminDto? LatestRevision,
    Guid? PublishedRevisionId, bool IsPublished, bool HasPendingRevision, bool CounselRequired,
    string? PublicPath, string? PublicUrl, SeoReviewEventDto? LastReviewEvent);

public record SeoRevisionPreviewDto(
    Guid Id, DateTime GeneratedAt, SeoContentStatus ContentStatus, string? PromptVersion, string SourceDataVersion,
    string BodyHtml);

public record SeoPageAdminDetailDto(
    SeoPageAdminDto Page, SeoRevisionPreviewDto? PublishedRevision, SeoRevisionPreviewDto? PendingRevision,
    IReadOnlyList<SeoReviewEventDto> ReviewHistory);

public record SeoApproveRevisionCommand(Guid PageId, Guid RevisionId, bool CounselApproved, string? Note, string ActorUserId);

public record SeoWithdrawCommand(Guid PageId, string? Note, string ActorUserId);

public record SaveSeoDraftCommand(Guid PageId, string BodyHtml, string? Title, string? MetaDescription, string ActorUserId);

public record SeoGenerateRequestDto(
    IReadOnlyList<string> ComuneCodes,
    IReadOnlyList<SeoPageType>? PageTypes,
    bool ForceRegenerate);
public record SeoGenerateAcceptedDto(string JobId, DateTime EnqueuedAt, int ComuneCount, int EstimatedPages);
public record PlatformAiBudgetDto(long MonthlyTokenCap, long TokensUsedThisMonth, DateTime LastResetAt);

public static class SeoReviewErrorCodes
{
    public const string CounselRequired = "seo_counsel_required";

    public const string RevisionNotPublishable = "seo_revision_not_publishable";

    public const string RevisionOutdated = "seo_revision_outdated";

    public const string RevisionAlreadyPublished = "seo_revision_already_published";

    public const string PageNotPublished = "seo_page_not_published";

    public const string DraftInvalid = "seo_draft_invalid";
}

public interface ISeoContentService
{
    Task<SeoPagePublicDto?> GetComplianceGuideAsync(string regionSlug, string comuneSlug, CancellationToken cancellationToken = default);
    Task<SeoPagePublicDto?> GetTouristTaxPageAsync(string comuneSlug, CancellationToken cancellationToken = default);
    Task<PublicTouristTaxCalculateResponse?> CalculateTouristTaxAsync(PublicTouristTaxCalculateRequest request, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<SeoPageAdminDto> Items, int TotalCount)> ListPagesAsync(LegalReviewStatus? legalReviewStatus, SeoPageType? pageType, string? comuneCode, int page, int pageSize, CancellationToken cancellationToken = default);

    Task<SeoPageAdminDetailDto?> GetAdminPageAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<SeoPageAdminDto> ApproveRevisionAsync(SeoApproveRevisionCommand command, CancellationToken cancellationToken = default);

    Task<SeoPageAdminDto> WithdrawAsync(SeoWithdrawCommand command, CancellationToken cancellationToken = default);

    Task<SeoPageAdminDetailDto> SaveManualDraftAsync(SaveSeoDraftCommand command, CancellationToken cancellationToken = default);

    Task<PlatformAiBudgetDto> GetPlatformAiBudgetAsync(CancellationToken cancellationToken = default);

    Task<int> GeneratePagesForComuneBatchAsync(IReadOnlyList<string> comuneCodes, IReadOnlyList<SeoPageType> pageTypes, bool forceRegenerate, CancellationToken cancellationToken = default);
    Task<int> RefreshStalePagesAsync(CancellationToken cancellationToken = default);
    Task<SeoPublishedPagesDto> GetPublishedPagesAsync(CancellationToken cancellationToken = default);

    Task<string> BuildComplianceSitemapXmlAsync(CancellationToken cancellationToken = default);
}
