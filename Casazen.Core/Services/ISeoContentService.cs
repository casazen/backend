using Casazen.Core.Entities.Enums;
using Casazen.Core.TouristTax;

namespace Casazen.Core.Services;

public record SeoDisclaimersDto(string LastUpdated, string NotLegalAdvice, string AiGenerated);
public record SeoCtaDto(string ComplianceCheckerUrl, string SignupUrl);
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
    string CanonicalUrl, DateTime? LastRefreshedAt, SeoDisclaimersDto Disclaimers, SeoCtaDto Cta,
    IReadOnlyList<PublicTouristTaxRateSummaryDto> TouristTaxRates);

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

public record SeoRevisionAdminDto(DateTime GeneratedAt, string AiModelTier, int PromptTokens, string SourceDataVersion);

public record SeoPageAdminDto(
    Guid Id, string Slug, string ComuneCode, string ComuneName, string RegionCode, string RegionSlug,
    SeoPageType PageType, string Title, LegalReviewStatus LegalReviewStatus,
    DateTime? PublishedAt, DateTime? LastRefreshedAt, SeoRevisionAdminDto? LatestRevision);

public record SeoGenerateRequestDto(
    IReadOnlyList<string> ComuneCodes,
    IReadOnlyList<SeoPageType>? PageTypes,
    bool ForceRegenerate,
    bool AutoApproveCounsel = false);
public record SeoGenerateAcceptedDto(string JobId, DateTime EnqueuedAt, int ComuneCount, int EstimatedPages);
public record PlatformAiBudgetDto(long MonthlyTokenCap, long TokensUsedThisMonth, DateTime LastResetAt);

public interface ISeoContentService
{
    Task<SeoPagePublicDto?> GetComplianceGuideAsync(string regionSlug, string comuneSlug, bool allowDraft, CancellationToken cancellationToken = default);
    Task<SeoPagePublicDto?> GetTouristTaxPageAsync(string comuneSlug, bool allowDraft, CancellationToken cancellationToken = default);
    Task<PublicTouristTaxCalculateResponse?> CalculateTouristTaxAsync(PublicTouristTaxCalculateRequest request, CancellationToken cancellationToken = default);
    Task<(IReadOnlyList<SeoPageAdminDto> Items, int TotalCount)> ListPagesAsync(LegalReviewStatus? legalReviewStatus, SeoPageType? pageType, string? comuneCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SeoPageAdminDto?> UpdateReviewStatusAsync(Guid pageId, LegalReviewStatus status, bool counselApproved, CancellationToken cancellationToken = default);
    Task<PlatformAiBudgetDto> GetPlatformAiBudgetAsync(CancellationToken cancellationToken = default);
    Task<int> GeneratePagesForComuneBatchAsync(IReadOnlyList<string> comuneCodes, IReadOnlyList<SeoPageType> pageTypes, bool forceRegenerate, CancellationToken cancellationToken = default);
    Task<int> ApproveAllDraftPagesAsync(bool counselApproved, CancellationToken cancellationToken = default);
    Task<int> RefreshStalePagesAsync(CancellationToken cancellationToken = default);
    Task<string> BuildComplianceSitemapXmlAsync(CancellationToken cancellationToken = default);
}
