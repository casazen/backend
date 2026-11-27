using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

public static class RegulatoryReferenceDataErrorCodes
{
    public const string AgreementNotFound = "ltr_agreement_not_found";
    public const string BandNotFound = "ltr_band_not_found";
    public const string ImuChannelNotFound = "ltr_imu_channel_not_found";
    public const string InvalidBandRange = "ltr_invalid_band_range";
    public const string VerifiedAtInFuture = "ltr_verified_at_in_future";
}

public sealed record ConcordatoRentBandDto(
    Guid Id,
    string ZoneName,
    string? CadastralSheets,
    int MinSqm,
    int? MaxSqm,
    decimal SubFascia1MinEurSqmYear,
    decimal SubFascia1MaxEurSqmYear,
    decimal SubFascia2MinEurSqmYear,
    decimal SubFascia2MaxEurSqmYear,
    decimal SubFascia3MinEurSqmYear,
    decimal SubFascia3MaxEurSqmYear);

public sealed record AgreementRulesDto(
    int RequiredTypeACount,
    int SubFascia2MinTypeBCount,
    int SubFascia3MinTypeCCount,
    int SubFascia3MinQualifyingTypeDCount,
    string? SubFascia3QualifyingTypeDElements,
    int SubFascia3MaxMinTypeDCount,
    int StoveHeatingMinTypeBCount,
    CoefficientCombination CoefficientCombination,
    decimal FurnishedUpliftPercent,
    decimal AirConditioningUpliftPercent,
    int SmallSqmMax,
    decimal SmallSqmUpliftPercent,
    int MidSqmMin,
    int MidSqmMax,
    decimal MidSqmUpliftPercent,
    int LargeSqmMin,
    decimal LargeSqmReductionPercent,
    decimal GarageAppurtenancePercent,
    decimal BalconyAppurtenancePercent,
    decimal OtherAppurtenancePercent,
    decimal GreenAreaAppurtenancePercent,
    decimal Duration4UpliftPercent,
    decimal Duration5UpliftPercent,
    decimal Duration6UpliftPercent);

public sealed record AdminAgreementSummaryDto(
    Guid Id,
    string Comune,
    string Region,
    string AgreementName,
    DataCompleteness DataCompleteness,
    int BandCount,
    IReadOnlyList<string> ZoneNames,
    string? SourceUrl,
    DateTime? LastVerifiedAt,
    string? VerificationSource,
    DateTime? ExpiresAt,
    bool RemainsInForceUntilReplaced,
    DateTime? UpdatedAt);

public sealed record AdminSignatoryDto(string Name, SignatoryRole Role, string Contact);

public sealed record AdminAgreementDetailDto(
    AdminAgreementSummaryDto Summary,
    AgreementRulesDto Rules,
    DateTime SignedDate,
    DateTime EffectiveDate,
    string? ExpiryNote,
    IReadOnlyList<ConcordatoRentBandDto> Bands,
    IReadOnlyList<AdminSignatoryDto> Signatories);

public sealed record UpdateAgreementInput(
    DataCompleteness DataCompleteness,
    string? SourceUrl,
    DateTime? ExpiresAt,
    string? ExpiryNote,
    bool RemainsInForceUntilReplaced,
    int RequiredTypeACount,
    int SubFascia2MinTypeBCount,
    int SubFascia3MinTypeCCount,
    int SubFascia3MinQualifyingTypeDCount,
    string? SubFascia3QualifyingTypeDElements,
    int SubFascia3MaxMinTypeDCount,
    int StoveHeatingMinTypeBCount,
    CoefficientCombination CoefficientCombination,
    decimal FurnishedUpliftPercent,
    decimal AirConditioningUpliftPercent,
    int SmallSqmMax,
    decimal SmallSqmUpliftPercent,
    int MidSqmMin,
    int MidSqmMax,
    decimal MidSqmUpliftPercent,
    int LargeSqmMin,
    decimal LargeSqmReductionPercent,
    decimal GarageAppurtenancePercent,
    decimal BalconyAppurtenancePercent,
    decimal OtherAppurtenancePercent,
    decimal GreenAreaAppurtenancePercent,
    decimal Duration4UpliftPercent,
    decimal Duration5UpliftPercent,
    decimal Duration6UpliftPercent);

public sealed record UpdateRentBandInput(
    string ZoneName,
    string? CadastralSheets,
    int MinSqm,
    int? MaxSqm,
    decimal SubFascia1MinEurSqmYear,
    decimal SubFascia1MaxEurSqmYear,
    decimal SubFascia2MinEurSqmYear,
    decimal SubFascia2MaxEurSqmYear,
    decimal SubFascia3MinEurSqmYear,
    decimal SubFascia3MaxEurSqmYear);

public sealed record AdminImuChannelDto(
    Guid Id,
    string Comune,
    string Region,
    string RecipientOffice,
    string? Email,
    string? Pec,
    string? PostalAddress,
    string? Instructions,
    decimal? RatePercent,
    decimal? EffectiveRatePercent,
    int? RateYear,
    ImuRateKind? RateKind,
    string? RateNotes,
    string? RateSourceUrl,
    string? SourceUrl,
    DataCompleteness DataCompleteness,
    DateTime? LastVerifiedAt,
    string? VerificationSource,
    DateTime? UpdatedAt);

public sealed record UpdateImuChannelInput(
    string RecipientOffice,
    string? Email,
    string? Pec,
    string? PostalAddress,
    string? Instructions,
    decimal? RatePercent,
    decimal? EffectiveRatePercent,
    int? RateYear,
    ImuRateKind? RateKind,
    string? RateNotes,
    string? RateSourceUrl,
    string? SourceUrl,
    DataCompleteness DataCompleteness);

public sealed record MarkVerifiedInput(DateOnly VerifiedAt, string Source);

public sealed record RegulatoryAuditEntryDto(
    string EntityType,
    Guid EntityId,
    RegulatoryAuditAction Action,
    string ChangedByUserId,
    DateTime OccurredAt,
    string Changes);

public interface IRegulatoryReferenceDataAdminService
{
    Task<IReadOnlyList<AdminAgreementSummaryDto>> GetAgreementsAsync(CancellationToken cancellationToken = default);

    Task<AdminAgreementDetailDto?> GetAgreementAsync(Guid id, CancellationToken cancellationToken = default);

    Task<AdminAgreementDetailDto?> UpdateAgreementAsync(
        Guid id, UpdateAgreementInput input, string changedByUserId, CancellationToken cancellationToken = default);

    Task<AdminAgreementDetailDto?> UpdateBandAsync(
        Guid agreementId, Guid bandId, UpdateRentBandInput input, string changedByUserId, CancellationToken cancellationToken = default);

    Task<AdminAgreementDetailDto?> MarkAgreementVerifiedAsync(
        Guid id, MarkVerifiedInput input, string changedByUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdminImuChannelDto>> GetImuChannelsAsync(CancellationToken cancellationToken = default);

    Task<AdminImuChannelDto?> UpdateImuChannelAsync(
        Guid id, UpdateImuChannelInput input, string changedByUserId, CancellationToken cancellationToken = default);

    Task<AdminImuChannelDto?> MarkImuChannelVerifiedAsync(
        Guid id, MarkVerifiedInput input, string changedByUserId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RegulatoryAuditEntryDto>> GetAuditAsync(Guid entityId, CancellationToken cancellationToken = default);
}
