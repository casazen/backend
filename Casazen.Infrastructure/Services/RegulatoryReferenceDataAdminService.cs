using System.Globalization;
using System.Reflection;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Core.Utilities;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Admin CRUD of the LTR regulatory reference data (LT-13, A7-22). Every write logs a
/// <see cref="RegulatoryDataAuditEntry"/> built as a plain field-by-field diff against the row's previous values, so
/// the trail never depends on the caller remembering what changed.
/// </summary>
public class RegulatoryReferenceDataAdminService(
    ITerritorialRentAgreementRepository agreements,
    IComuneImuChannelRepository imuChannels,
    IRegulatoryDataAuditLogRepository auditLog,
    TimeProvider timeProvider) : IRegulatoryReferenceDataAdminService
{
    private const string AgreementEntityType = "TerritorialRentAgreement";
    private const string ImuChannelEntityType = "ComuneImuChannel";

    public async Task<IReadOnlyList<AdminAgreementSummaryDto>> GetAgreementsAsync(CancellationToken cancellationToken = default)
    {
        var all = await agreements.GetAllAsync(cancellationToken);
        return all.Select(ToSummaryDto).ToList();
    }

    public async Task<AdminAgreementDetailDto?> GetAgreementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var agreement = await agreements.GetByIdAsync(id, cancellationToken);
        return agreement is null ? null : ToDetailDto(agreement);
    }

    public async Task<AdminAgreementDetailDto?> UpdateAgreementAsync(
        Guid id, UpdateAgreementInput input, string changedByUserId, CancellationToken cancellationToken = default)
    {
        var agreement = await agreements.GetByIdAsync(id, cancellationToken);
        if (agreement is null)
            return null;

        var before = ToUpdateInput(agreement);

        agreement.DataCompleteness = input.DataCompleteness;
        agreement.SourceUrl = input.SourceUrl ?? string.Empty;
        agreement.ExpiresAt = input.ExpiresAt;
        agreement.ExpiryNote = input.ExpiryNote;
        agreement.RemainsInForceUntilReplaced = input.RemainsInForceUntilReplaced;
        agreement.RequiredTypeACount = input.RequiredTypeACount;
        agreement.SubFascia2MinTypeBCount = input.SubFascia2MinTypeBCount;
        agreement.SubFascia3MinTypeCCount = input.SubFascia3MinTypeCCount;
        agreement.SubFascia3MinQualifyingTypeDCount = input.SubFascia3MinQualifyingTypeDCount;
        agreement.SubFascia3QualifyingTypeDElements = input.SubFascia3QualifyingTypeDElements;
        agreement.SubFascia3MaxMinTypeDCount = input.SubFascia3MaxMinTypeDCount;
        agreement.StoveHeatingMinTypeBCount = input.StoveHeatingMinTypeBCount;
        agreement.CoefficientCombination = input.CoefficientCombination;
        agreement.FurnishedUpliftPercent = input.FurnishedUpliftPercent;
        agreement.AirConditioningUpliftPercent = input.AirConditioningUpliftPercent;
        agreement.SmallSqmMax = input.SmallSqmMax;
        agreement.SmallSqmUpliftPercent = input.SmallSqmUpliftPercent;
        agreement.MidSqmMin = input.MidSqmMin;
        agreement.MidSqmMax = input.MidSqmMax;
        agreement.MidSqmUpliftPercent = input.MidSqmUpliftPercent;
        agreement.LargeSqmMin = input.LargeSqmMin;
        agreement.LargeSqmReductionPercent = input.LargeSqmReductionPercent;
        agreement.GarageAppurtenancePercent = input.GarageAppurtenancePercent;
        agreement.BalconyAppurtenancePercent = input.BalconyAppurtenancePercent;
        agreement.OtherAppurtenancePercent = input.OtherAppurtenancePercent;
        agreement.GreenAreaAppurtenancePercent = input.GreenAreaAppurtenancePercent;
        agreement.Duration4UpliftPercent = input.Duration4UpliftPercent;
        agreement.Duration5UpliftPercent = input.Duration5UpliftPercent;
        agreement.Duration6UpliftPercent = input.Duration6UpliftPercent;
        agreement.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await agreements.SaveChangesAsync(cancellationToken);
        await LogAsync(AgreementEntityType, agreement.Id, RegulatoryAuditAction.Updated, changedByUserId,
            DescribeChanges(before, input), cancellationToken);

        return ToDetailDto(agreement);
    }

    public async Task<AdminAgreementDetailDto?> UpdateBandAsync(
        Guid agreementId, Guid bandId, UpdateRentBandInput input, string changedByUserId,
        CancellationToken cancellationToken = default)
    {
        var band = await agreements.GetBandByIdAsync(agreementId, bandId, cancellationToken);
        if (band is null)
            return null;

        if (input.MaxSqm is { } maxSqm && maxSqm <= input.MinSqm)
        {
            throw new DomainRuleException(
                RegulatoryReferenceDataErrorCodes.InvalidBandRange, "LtrInvalidBandRange");
        }

        var before = ToUpdateInput(band);

        band.ZoneName = input.ZoneName.Trim();
        band.CadastralSheets = string.IsNullOrWhiteSpace(input.CadastralSheets) ? null : input.CadastralSheets.Trim();
        band.MinSqm = input.MinSqm;
        band.MaxSqm = input.MaxSqm;
        band.SubFascia1MinEurSqmYear = input.SubFascia1MinEurSqmYear;
        band.SubFascia1MaxEurSqmYear = input.SubFascia1MaxEurSqmYear;
        band.SubFascia2MinEurSqmYear = input.SubFascia2MinEurSqmYear;
        band.SubFascia2MaxEurSqmYear = input.SubFascia2MaxEurSqmYear;
        band.SubFascia3MinEurSqmYear = input.SubFascia3MinEurSqmYear;
        band.SubFascia3MaxEurSqmYear = input.SubFascia3MaxEurSqmYear;

        var agreement = await agreements.GetByIdAsync(agreementId, cancellationToken);
        if (agreement is not null)
            agreement.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await agreements.SaveChangesAsync(cancellationToken);
        // Logged under the agreement's id (not the band's) so it shows in the agreement's own audit trail.
        await LogAsync(AgreementEntityType, agreementId, RegulatoryAuditAction.Updated, changedByUserId,
            $"Fascia '{before.ZoneName}':\n" + DescribeChanges(before, input), cancellationToken);

        return await GetAgreementAsync(agreementId, cancellationToken);
    }

    public async Task<AdminAgreementDetailDto?> MarkAgreementVerifiedAsync(
        Guid id, MarkVerifiedInput input, string changedByUserId, CancellationToken cancellationToken = default)
    {
        EnsureNotInFuture(input.VerifiedAt);

        var agreement = await agreements.GetByIdAsync(id, cancellationToken);
        if (agreement is null)
            return null;

        agreement.LastVerifiedAt = input.VerifiedAt.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        agreement.VerificationSource = input.Source.Trim();
        agreement.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await agreements.SaveChangesAsync(cancellationToken);
        await LogAsync(AgreementEntityType, agreement.Id, RegulatoryAuditAction.MarkedVerified, changedByUserId,
            $"verifiedAt: {input.VerifiedAt:yyyy-MM-dd}; source: {agreement.VerificationSource}", cancellationToken);

        return ToDetailDto(agreement);
    }

    public async Task<IReadOnlyList<AdminImuChannelDto>> GetImuChannelsAsync(CancellationToken cancellationToken = default)
    {
        var all = await imuChannels.GetAllAsync(cancellationToken);
        return all.Select(ToDto).ToList();
    }

    public async Task<AdminImuChannelDto?> UpdateImuChannelAsync(
        Guid id, UpdateImuChannelInput input, string changedByUserId, CancellationToken cancellationToken = default)
    {
        var channel = await imuChannels.GetByIdAsync(id, cancellationToken);
        if (channel is null)
            return null;

        var before = ToUpdateInput(channel);

        channel.RecipientOffice = input.RecipientOffice.Trim();
        channel.Email = input.Email;
        channel.Pec = input.Pec;
        channel.PostalAddress = input.PostalAddress;
        channel.Instructions = input.Instructions;
        channel.RatePercent = input.RatePercent;
        channel.EffectiveRatePercent = input.EffectiveRatePercent;
        channel.RateYear = input.RateYear;
        channel.RateKind = input.RateKind;
        channel.RateNotes = input.RateNotes;
        channel.RateSourceUrl = input.RateSourceUrl;
        channel.SourceUrl = input.SourceUrl;
        channel.DataCompleteness = input.DataCompleteness;
        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await imuChannels.SaveChangesAsync(cancellationToken);
        await LogAsync(ImuChannelEntityType, channel.Id, RegulatoryAuditAction.Updated, changedByUserId,
            DescribeChanges(before, input), cancellationToken);

        return ToDto(channel);
    }

    public async Task<AdminImuChannelDto?> MarkImuChannelVerifiedAsync(
        Guid id, MarkVerifiedInput input, string changedByUserId, CancellationToken cancellationToken = default)
    {
        EnsureNotInFuture(input.VerifiedAt);

        var channel = await imuChannels.GetByIdAsync(id, cancellationToken);
        if (channel is null)
            return null;

        channel.LastVerifiedAt = input.VerifiedAt.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        channel.VerificationSource = input.Source.Trim();
        channel.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;

        await imuChannels.SaveChangesAsync(cancellationToken);
        await LogAsync(ImuChannelEntityType, channel.Id, RegulatoryAuditAction.MarkedVerified, changedByUserId,
            $"verifiedAt: {input.VerifiedAt:yyyy-MM-dd}; source: {channel.VerificationSource}", cancellationToken);

        return ToDto(channel);
    }

    public async Task<IReadOnlyList<RegulatoryAuditEntryDto>> GetAuditAsync(
        Guid entityId, CancellationToken cancellationToken = default)
    {
        var entries = await auditLog.GetByEntityIdAsync(entityId, cancellationToken);
        return entries
            .Select(e => new RegulatoryAuditEntryDto(e.EntityType, e.EntityId, e.Action, e.ChangedByUserId, e.OccurredAt, e.Changes))
            .ToList();
    }

    private void EnsureNotInFuture(DateOnly verifiedAt)
    {
        if (verifiedAt > timeProvider.TodayInRomeAsDateOnly())
        {
            throw new DomainRuleException(
                RegulatoryReferenceDataErrorCodes.VerifiedAtInFuture, "LtrVerifiedAtInFuture");
        }
    }

    private async Task LogAsync(
        string entityType, Guid entityId, RegulatoryAuditAction action, string changedByUserId, string changes,
        CancellationToken cancellationToken)
    {
        await auditLog.AddAsync(new RegulatoryDataAuditEntry
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            ChangedByUserId = changedByUserId,
            OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
            Changes = changes,
        }, cancellationToken);
    }

    private static AdminAgreementSummaryDto ToSummaryDto(TerritorialRentAgreement agreement) => new(
        agreement.Id,
        agreement.Comune,
        agreement.Region,
        agreement.AgreementName,
        agreement.DataCompleteness,
        agreement.Bands.Count,
        agreement.Bands.Select(b => b.ZoneName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        NullIfBlank(agreement.SourceUrl),
        agreement.LastVerifiedAt,
        agreement.VerificationSource,
        agreement.ExpiresAt,
        agreement.RemainsInForceUntilReplaced,
        agreement.UpdatedAt);

    private static AdminAgreementDetailDto ToDetailDto(TerritorialRentAgreement agreement) => new(
        ToSummaryDto(agreement),
        new AgreementRulesDto(
            agreement.RequiredTypeACount,
            agreement.SubFascia2MinTypeBCount,
            agreement.SubFascia3MinTypeCCount,
            agreement.SubFascia3MinQualifyingTypeDCount,
            agreement.SubFascia3QualifyingTypeDElements,
            agreement.SubFascia3MaxMinTypeDCount,
            agreement.StoveHeatingMinTypeBCount,
            agreement.CoefficientCombination,
            agreement.FurnishedUpliftPercent,
            agreement.AirConditioningUpliftPercent,
            agreement.SmallSqmMax,
            agreement.SmallSqmUpliftPercent,
            agreement.MidSqmMin,
            agreement.MidSqmMax,
            agreement.MidSqmUpliftPercent,
            agreement.LargeSqmMin,
            agreement.LargeSqmReductionPercent,
            agreement.GarageAppurtenancePercent,
            agreement.BalconyAppurtenancePercent,
            agreement.OtherAppurtenancePercent,
            agreement.GreenAreaAppurtenancePercent,
            agreement.Duration4UpliftPercent,
            agreement.Duration5UpliftPercent,
            agreement.Duration6UpliftPercent),
        agreement.SignedDate,
        agreement.EffectiveDate,
        agreement.ExpiryNote,
        agreement.Bands
            .OrderBy(b => b.ZoneName).ThenBy(b => b.MinSqm)
            .Select(b => new ConcordatoRentBandDto(
                b.Id, b.ZoneName, b.CadastralSheets, b.MinSqm, b.MaxSqm,
                b.SubFascia1MinEurSqmYear, b.SubFascia1MaxEurSqmYear,
                b.SubFascia2MinEurSqmYear, b.SubFascia2MaxEurSqmYear,
                b.SubFascia3MinEurSqmYear, b.SubFascia3MaxEurSqmYear))
            .ToList(),
        agreement.Signatories
            .Select(s => new AdminSignatoryDto(s.Name, s.Role, s.Contact))
            .ToList());

    private static UpdateAgreementInput ToUpdateInput(TerritorialRentAgreement agreement) => new(
        agreement.DataCompleteness,
        NullIfBlank(agreement.SourceUrl),
        agreement.ExpiresAt,
        agreement.ExpiryNote,
        agreement.RemainsInForceUntilReplaced,
        agreement.RequiredTypeACount,
        agreement.SubFascia2MinTypeBCount,
        agreement.SubFascia3MinTypeCCount,
        agreement.SubFascia3MinQualifyingTypeDCount,
        agreement.SubFascia3QualifyingTypeDElements,
        agreement.SubFascia3MaxMinTypeDCount,
        agreement.StoveHeatingMinTypeBCount,
        agreement.CoefficientCombination,
        agreement.FurnishedUpliftPercent,
        agreement.AirConditioningUpliftPercent,
        agreement.SmallSqmMax,
        agreement.SmallSqmUpliftPercent,
        agreement.MidSqmMin,
        agreement.MidSqmMax,
        agreement.MidSqmUpliftPercent,
        agreement.LargeSqmMin,
        agreement.LargeSqmReductionPercent,
        agreement.GarageAppurtenancePercent,
        agreement.BalconyAppurtenancePercent,
        agreement.OtherAppurtenancePercent,
        agreement.GreenAreaAppurtenancePercent,
        agreement.Duration4UpliftPercent,
        agreement.Duration5UpliftPercent,
        agreement.Duration6UpliftPercent);

    private static UpdateRentBandInput ToUpdateInput(ConcordatoRentBand band) => new(
        band.ZoneName,
        band.CadastralSheets,
        band.MinSqm,
        band.MaxSqm,
        band.SubFascia1MinEurSqmYear,
        band.SubFascia1MaxEurSqmYear,
        band.SubFascia2MinEurSqmYear,
        band.SubFascia2MaxEurSqmYear,
        band.SubFascia3MinEurSqmYear,
        band.SubFascia3MaxEurSqmYear);

    private static AdminImuChannelDto ToDto(ComuneImuChannel channel) => new(
        channel.Id, channel.Comune, channel.Region, channel.RecipientOffice,
        channel.Email, channel.Pec, channel.PostalAddress, channel.Instructions,
        channel.RatePercent, channel.EffectiveRatePercent, channel.RateYear, channel.RateKind,
        channel.RateNotes, channel.RateSourceUrl, channel.SourceUrl,
        channel.DataCompleteness, channel.LastVerifiedAt, channel.VerificationSource, channel.UpdatedAt);

    private static UpdateImuChannelInput ToUpdateInput(ComuneImuChannel channel) => new(
        channel.RecipientOffice, channel.Email, channel.Pec, channel.PostalAddress, channel.Instructions,
        channel.RatePercent, channel.EffectiveRatePercent, channel.RateYear, channel.RateKind,
        channel.RateNotes, channel.RateSourceUrl, channel.SourceUrl, channel.DataCompleteness);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Field-by-field diff of two records of the same shape, as "Name: old -> new" lines (only what changed).</summary>
    private static string DescribeChanges<T>(T before, T after)
    {
        var lines = new List<string>();
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var oldValue = property.GetValue(before);
            var newValue = property.GetValue(after);
            if (Equals(oldValue, newValue))
                continue;
            lines.Add($"{property.Name}: {FormatValue(oldValue)} -> {FormatValue(newValue)}");
        }
        return lines.Count > 0 ? string.Join('\n', lines) : "(nessuna modifica ai campi)";
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "-",
        DateTime dt => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "-",
    };
}
