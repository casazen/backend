using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Canone concordato range of a unit (LT-10, A7-10, A7-11), from the territorial agreement of the property's comune.
/// Every value comes from the agreement row; nothing normative is written here.
/// </summary>
/// <remarks>
/// Calculation (<c>.claude/context/regulations/canone_concordato.md</c>, "Cosa deve cambiare LT-10"; D = interpretation
/// to be confirmed by a signatory organization):
/// <list type="number">
/// <item>mq utili = surface + appurtenances at the agreement's percentages; the band is looked up on them rounded to the
/// whole metre (D), in contiguous half-open bands.</item>
/// <item>Sub-fascia: A-elements all present, stoves (sub-fascia 1 unless enough B), B, C and qualifying D thresholds.</item>
/// <item>Surface for the maximum: below the small threshold ×(1 + uplift) capped at it; strictly between the mid thresholds
/// ×(1 + uplift) capped at the upper one; above the large threshold ×(1 − reduction), never below it. The reduction is
/// "potrà" (D) and is applied, prudent on the maximum; the minimum uses the same reduced surface without uplifts.</item>
/// <item>Coefficients: furniture, term (4, 5, 6 years; none above 6), air conditioning, combined as the agreement says
/// (additive by default, D). The minimum gets only the mandatory term uplift (D).</item>
/// <item>Range: from the minimum of sub-fascia 1 (the parties may choose a lower sub-fascia, D) to the maximum of the
/// unit's sub-fascia. Monthly bounds are rounded inwards, so twelve months never leave the annual range.</item>
/// </list>
/// </remarks>
public class CanoneConcordatoEligibilityService(
    ITerritorialRentAgreementRepository agreements,
    IHighTensionAreaComuneRepository ataComuni,
    IPropertyRepository properties) : ICanoneConcordatoEligibilityService
{
    private const int SixYearsInMonths = 72;

    public async Task<CanoneConcordatoEligibilityDto?> CalculateAsync(
        Guid propertyId,
        RentBandCharacteristics characteristics,
        LeaseTerm term,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        var property = await properties.GetByIdAsync(propertyId);
        if (property is null)
            return null;

        var contractYears = term.Months / 12;

        if (characteristics.Sqm < 1m || HasNegativeAppurtenance(characteristics))
            return Unavailable(property.City, characteristics.ZoneName, null, contractYears,
                CanoneConcordatoReasonCodes.InvalidSurface, CanoneConcordatoCopy.ReasonInvalidSqm);

        var agreement = await agreements.GetByComuneAsync(property.City, cancellationToken);
        if (agreement is null || agreement.DataCompleteness == DataCompleteness.Missing || agreement.Bands.Count == 0)
        {
            return Unavailable(property.City, null, agreement?.DataCompleteness ?? DataCompleteness.Missing, contractYears,
                CanoneConcordatoReasonCodes.DataUnavailable, CanoneConcordatoCopy.ReasonDataUnavailable);
        }

        if (term.Months < LeaseContractTerms.ConcordatoMinimumMonths)
        {
            return Unavailable(property.City, characteristics.ZoneName, agreement.DataCompleteness, contractYears,
                CanoneConcordatoReasonCodes.TermTooShort, CanoneConcordatoCopy.ReasonTermTooShort);
        }

        if (characteristics.QualifyingTypeDElementCount > characteristics.TypeDElementCount)
        {
            return Unavailable(property.City, characteristics.ZoneName, agreement.DataCompleteness, contractYears,
                CanoneConcordatoReasonCodes.InvalidElementCounts, CanoneConcordatoCopy.ReasonInvalidElementCounts);
        }

        var usableSqm = UsableSqm(agreement, characteristics);
        var sheet = string.IsNullOrWhiteSpace(characteristics.CadastralSheet)
            ? property.CadastralSheet
            : characteristics.CadastralSheet;
        var lookup = ResolveBand(agreement, characteristics.ZoneName, sheet, BandSqm(usableSqm));
        if (lookup.Band is not { } band)
        {
            var (code, text) = lookup.Failure switch
            {
                CanoneConcordatoReasonCodes.ZoneRequired => (lookup.Failure, CanoneConcordatoCopy.ReasonZoneRequired),
                CanoneConcordatoReasonCodes.ZoneNotFound => (lookup.Failure, CanoneConcordatoCopy.ReasonZoneNotFound),
                _ => (CanoneConcordatoReasonCodes.SurfaceOutOfBands, CanoneConcordatoCopy.ReasonSurfaceOutOfBands),
            };
            return Unavailable(property.City, characteristics.ZoneName, agreement.DataCompleteness, contractYears, code, text) with
            {
                UsableSqm = RoundSqm(usableSqm),
            };
        }

        var warnings = new List<string>();
        if (agreement.DataCompleteness != DataCompleteness.Complete)
            warnings.Add(CanoneConcordatoWarningCodes.PartialData);

        var subFascia = DetermineSubFascia(agreement, characteristics);
        if (subFascia == 3 && characteristics.TypeDElementCount < agreement.SubFascia3MaxMinTypeDCount)
            warnings.Add(CanoneConcordatoWarningCodes.SubFascia3MaxNeedsMoreTypeD);

        var durationPercent = DurationUpliftPercent(agreement, term);
        if (LeaseContractTerms.IsLongerThan(term, SixYearsInMonths))
            warnings.Add(CanoneConcordatoWarningCodes.NoDurationUpliftOverSixYears);

        var reducedSqm = ReducedSqm(agreement, usableSqm);
        var maxSqm = UpliftedSqm(agreement, usableSqm) ?? reducedSqm;
        var maxFactor = Combine(
            agreement.CoefficientCombination,
            durationPercent,
            characteristics.IsFurnished ? agreement.FurnishedUpliftPercent : 0m,
            characteristics.AirConditioning ? agreement.AirConditioningUpliftPercent : 0m);
        var minFactor = Combine(agreement.CoefficientCombination, durationPercent);

        var minAnnuo = RoundMoney(band.SubFascia1MinEurSqmYear * reducedSqm * minFactor);
        var maxAnnuo = RoundMoney(MaxRate(band, subFascia) * maxSqm * maxFactor);

        var ata = await ataComuni.GetByComuneAsync(property.City, cancellationToken);
        var ataApplies = ata is { VerifiedDirectly: true };

        return new CanoneConcordatoEligibilityDto(
            true,
            null,
            property.City,
            band.ZoneName,
            subFascia,
            minAnnuo,
            maxAnnuo,
            CeilingCents(minAnnuo / 12m),
            FloorCents(maxAnnuo / 12m),
            agreement.DataCompleteness,
            ImuAppliesTheoretical: true,
            ataApplies,
            AttestationRequired: true,
            CanoneConcordatoCopy.Disclaimer)
        {
            ContractYears = contractYears,
            UsableSqm = RoundSqm(usableSqm),
            BandMinSqm = band.MinSqm,
            BandMaxSqm = band.MaxSqm,
            Indicative = agreement.DataCompleteness != DataCompleteness.Complete,
            Warnings = warnings,
            SourceUrl = NullIfBlank(agreement.SourceUrl),
            LastVerifiedAt = agreement.LastVerifiedAt,
            SubFascia3QualifyingTypeDElements = agreement.SubFascia3QualifyingTypeDElements,
        };
    }

    private static CanoneConcordatoEligibilityDto Unavailable(
        string comune, string? zone, DataCompleteness? completeness, int contractYears, string code, string reason) =>
        new(false, reason, comune, zone, null, null, null, null, null,
            completeness, false, false, true, CanoneConcordatoCopy.Disclaimer)
        {
            ReasonCode = code,
            ContractYears = contractYears,
            Indicative = completeness is not DataCompleteness.Complete,
        };

    private static bool HasNegativeAppurtenance(RentBandCharacteristics c) =>
        c.GarageSqm < 0 || c.BalconySqm < 0 || c.OtherAppurtenanceSqm < 0 || c.PrivateGreenSqm < 0;

    /// <summary>Surface plus the appurtenances leased with it, at the agreement's percentages.</summary>
    private static decimal UsableSqm(TerritorialRentAgreement agreement, RentBandCharacteristics c) =>
        c.Sqm
        + (c.GarageSqm * agreement.GarageAppurtenancePercent / 100m)
        + (c.BalconySqm * agreement.BalconyAppurtenancePercent / 100m)
        + (c.OtherAppurtenanceSqm * agreement.OtherAppurtenancePercent / 100m)
        + (c.PrivateGreenSqm * agreement.GreenAreaAppurtenancePercent / 100m);

    /// <summary>Square metres the band is looked up on: whole metres, 0,5 up (class D).</summary>
    private static decimal BandSqm(decimal usableSqm) => decimal.Round(usableSqm, 0, MidpointRounding.AwayFromZero);

    private sealed record BandLookup(ConcordatoRentBand? Band, string? Failure);

    private static BandLookup ResolveBand(
        TerritorialRentAgreement agreement, string? zoneName, string? sheet, decimal bandSqm)
    {
        var zoneNames = agreement.Bands.Select(b => b.ZoneName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var zoneProvided = !string.IsNullOrWhiteSpace(zoneName);
        var sheetProvided = !string.IsNullOrWhiteSpace(sheet);
        var candidates = agreement.Bands.AsEnumerable();

        if (zoneNames.Count > 1 && !zoneProvided && !sheetProvided)
            return new BandLookup(null, CanoneConcordatoReasonCodes.ZoneRequired);

        if (zoneProvided || sheetProvided)
        {
            candidates = candidates.Where(b => MatchesZoneAndSheet(b, zoneName, sheet)).ToList();
            if (!candidates.Any())
                return new BandLookup(null, CanoneConcordatoReasonCodes.ZoneNotFound);
        }

        var band = candidates.Where(b => b.Contains(bandSqm)).OrderBy(b => b.MinSqm).FirstOrDefault();
        return new BandLookup(band, band is null ? CanoneConcordatoReasonCodes.SurfaceOutOfBands : null);
    }

    private static bool MatchesZoneAndSheet(ConcordatoRentBand band, string? zoneName, string? sheet)
    {
        if (!string.IsNullOrWhiteSpace(zoneName) &&
            !string.Equals(band.ZoneName, zoneName.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        // A zone defined without sheets (Seveso: the whole comune) holds every sheet.
        if (string.IsNullOrWhiteSpace(sheet) || string.IsNullOrWhiteSpace(band.CadastralSheets))
            return true;

        var trimmed = sheet.Trim();
        return band.CadastralSheets
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(s => string.Equals(s, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Sub-fascia 1 when an A-element is missing, when heating is by stoves without enough B-elements, or with too few
    /// B-elements; sub-fascia 3 with enough C and qualifying D elements; sub-fascia 2 otherwise.
    /// </summary>
    private static int DetermineSubFascia(TerritorialRentAgreement agreement, RentBandCharacteristics c)
    {
        if (c.TypeAElementCount < agreement.RequiredTypeACount)
            return 1;

        if (c.StoveHeating && agreement.StoveHeatingMinTypeBCount > 0 && c.TypeBElementCount < agreement.StoveHeatingMinTypeBCount)
            return 1;

        if (c.TypeBElementCount < agreement.SubFascia2MinTypeBCount)
            return 1;

        var fascia3 = c.TypeCElementCount >= agreement.SubFascia3MinTypeCCount
                      && c.QualifyingTypeDElementCount >= agreement.SubFascia3MinQualifyingTypeDCount;
        return fascia3 ? 3 : 2;
    }

    private static decimal MaxRate(ConcordatoRentBand band, int subFascia) => subFascia switch
    {
        2 => band.SubFascia2MaxEurSqmYear,
        3 => band.SubFascia3MaxEurSqmYear,
        _ => band.SubFascia1MaxEurSqmYear,
    };

    /// <summary>Surface after the reduction above the large threshold (never below it); otherwise unchanged.</summary>
    private static decimal ReducedSqm(TerritorialRentAgreement agreement, decimal usableSqm)
    {
        if (agreement.LargeSqmMin > 0 && usableSqm > agreement.LargeSqmMin)
            return Math.Max(usableSqm * (1m - (agreement.LargeSqmReductionPercent / 100m)), agreement.LargeSqmMin);

        return usableSqm;
    }

    /// <summary>Surface after the small or mid uplift, capped at its threshold; null when neither applies.</summary>
    private static decimal? UpliftedSqm(TerritorialRentAgreement agreement, decimal usableSqm)
    {
        if (agreement.SmallSqmMax > 0 && usableSqm < agreement.SmallSqmMax)
            return Math.Min(usableSqm * (1m + (agreement.SmallSqmUpliftPercent / 100m)), agreement.SmallSqmMax);

        if (agreement.MidSqmMin > 0 && agreement.MidSqmMax > agreement.MidSqmMin
            && usableSqm > agreement.MidSqmMin && usableSqm < agreement.MidSqmMax)
            return Math.Min(usableSqm * (1m + (agreement.MidSqmUpliftPercent / 100m)), agreement.MidSqmMax);

        return null;
    }

    /// <summary>Term uplift of 4, 5 or 6 whole years; none below 4 or above 6 years (the agreement has none).</summary>
    private static decimal DurationUpliftPercent(TerritorialRentAgreement agreement, LeaseTerm term)
    {
        if (LeaseContractTerms.IsLongerThan(term, SixYearsInMonths))
            return 0m;

        return (term.Months / 12) switch
        {
            4 => agreement.Duration4UpliftPercent,
            5 => agreement.Duration5UpliftPercent,
            6 => agreement.Duration6UpliftPercent,
            _ => 0m,
        };
    }

    private static decimal Combine(CoefficientCombination combination, params decimal[] percents) =>
        combination == CoefficientCombination.Multiplicative
            ? percents.Aggregate(1m, (factor, percent) => factor * (1m + (percent / 100m)))
            : 1m + (percents.Sum() / 100m);

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal RoundSqm(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal FloorCents(decimal value) => Math.Floor(value * 100m) / 100m;

    private static decimal CeilingCents(decimal value) => Math.Ceiling(value * 100m) / 100m;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
