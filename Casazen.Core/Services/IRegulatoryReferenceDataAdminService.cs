using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>Reasons an admin write on regulatory reference data is refused (LT-13, A7-22).</summary>
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

/// <summary>Every threshold and percentage of the rent calculation, stored on the agreement (A7-22): never a literal.</summary>
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

/// <summary>Body of <c>PUT .../agreements/{id}</c>: status, source, expiry and every rule. Never the verification date.</summary>
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

/// <summary>Body of <c>PUT .../agreements/{id}/bands/{bandId}</c>.</summary>
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

/// <summary>Body of <c>PUT .../imu-channels/{id}</c>: comune, region and verification are not editable here.</summary>
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

/// <summary>Body of <c>POST .../verify</c>: the date of the check (not in the future) and what it was checked against.</summary>
public sealed record MarkVerifiedInput(DateOnly VerifiedAt, string Source);

public sealed record RegulatoryAuditEntryDto(
    string EntityType,
    Guid EntityId,
    RegulatoryAuditAction Action,
    string ChangedByUserId,
    DateTime OccurredAt,
    string Changes);

/// <summary>
/// Admin CRUD of the LTR regulatory reference data (LT-13, A7-22): territorial agreements (status, expiry, rules and
/// bands) and the comune offices receiving the IMU communication. Every write is recorded in the audit log with the
/// admin's user id. Returns null when the id does not exist; the controller answers 404.
/// </summary>
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
