using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public record SeoDisclaimersDto(string LastUpdated, string NotLegalAdvice, string AiGenerated);
public record SeoCtaDto(string ComplianceCheckerUrl, string SignupUrl);
public record PublicTouristTaxRateSummaryDto(decimal RatePerPersonPerNight, int? MaxNights, int MinimumAge, string City);

public record SeoPagePublicDto(
    Guid Id, SeoPageType PageType, string Title, string MetaDescription, string BodyHtml,
    string ComuneName, string ComuneCode, string RegionCode, string RegionSlug, string ComuneSlug,
    string CanonicalUrl, DateTime? LastRefreshedAt, SeoDisclaimersDto Disclaimers, SeoCtaDto Cta,
    PublicTouristTaxRateSummaryDto? TouristTaxRate);

public record PublicTouristTaxCalculateRequest(
    string ComuneSlug, int NumberOfAdults, int NumberOfChildren,
    DateTime CheckInDate, DateTime CheckOutDate);

public record PublicTouristTaxCalculateResponse(
    string ComuneSlug, string City, decimal TaxAmount, int NumberOfAdults, int NumberOfChildren,
    int Nights, decimal RatePerPersonPerNight, int MaxNightsApplied,
    DateTime CheckInDate, DateTime CheckOutDate, string Disclaimer);

public record SeoRevisionAdminDto(DateTime GeneratedAt, string AiModelTier, int PromptTokens, string SourceDataVersion);

public record SeoPageAdminDto(
    Guid Id, string Slug, string ComuneCode, string ComuneName, string RegionCode, string RegionSlug,
    SeoPageType PageType, string Title, LegalReviewStatus LegalReviewStatus,
    DateTime? PublishedAt, DateTime? LastRefreshedAt, SeoRevisionAdminDto? LatestRevision);

public record SeoGenerateRequestDto(IReadOnlyList<string> ComuneCodes, IReadOnlyList<SeoPageType>? PageTypes, bool ForceRegenerate);
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
    Task<int> RefreshStalePagesAsync(CancellationToken cancellationToken = default);
    Task<string> BuildComplianceSitemapXmlAsync(CancellationToken cancellationToken = default);
}
