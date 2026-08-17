using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Repositories;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public class CanoneConcordatoEligibilityService(
    ITerritorialRentAgreementRepository agreements,
    IHighTensionAreaComuneRepository ataComuni,
    IPropertyRepository properties) : ICanoneConcordatoEligibilityService
{
    public async Task<CanoneConcordatoEligibilityDto?> CalculateAsync(
        Guid propertyId,
        string ownerId,
        RentBandCharacteristics characteristics,
        CancellationToken cancellationToken = default)
    {
        var property = await properties.GetByIdAsync(propertyId);
        if (property is null || property.OwnerId != ownerId)
            return null;

        if (characteristics.Sqm < 1m)
        {
            return new CanoneConcordatoEligibilityDto(
                false,
                CanoneConcordatoCopy.ReasonInvalidSqm,
                property.City,
                characteristics.ZoneName,
                null, null, null, null, null,
                null,
                false, false, true,
                CanoneConcordatoCopy.Disclaimer);
        }

        var agreement = await agreements.GetByComuneAsync(property.City, cancellationToken);
        if (agreement is null || agreement.DataCompleteness == DataCompleteness.Missing || agreement.Bands.Count == 0)
        {
            return Unavailable(property.City, agreement?.DataCompleteness ?? DataCompleteness.Missing);
        }

        var band = ResolveBand(agreement, characteristics);
        if (band is null)
        {
            return new CanoneConcordatoEligibilityDto(
                false,
                CanoneConcordatoCopy.ReasonZoneRequired,
                property.City,
                characteristics.ZoneName,
                null, null, null, null, null,
                agreement.DataCompleteness,
                false, false, true,
                CanoneConcordatoCopy.Disclaimer);
        }

        var subFascia = DetermineSubFascia(agreement, characteristics);
        var (minEur, maxEur) = RatesFor(band, subFascia);
        var factor = CoefficientFactor(agreement, characteristics);
        var minAnnuo = RoundMoney(minEur * characteristics.Sqm * factor);
        var maxAnnuo = RoundMoney(maxEur * characteristics.Sqm * factor);

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
            RoundMoney(minAnnuo / 12m),
            RoundMoney(maxAnnuo / 12m),
            agreement.DataCompleteness,
            ImuAppliesTheoretical: true,
            ataApplies,
            AttestationRequired: true,
            CanoneConcordatoCopy.Disclaimer);
    }

    private static CanoneConcordatoEligibilityDto Unavailable(string comune, DataCompleteness completeness) =>
        new(false, CanoneConcordatoCopy.ReasonDataUnavailable, comune, null, null, null, null, null, null,
            completeness, false, false, true, CanoneConcordatoCopy.Disclaimer);

    private static ConcordatoRentBand? ResolveBand(TerritorialRentAgreement agreement, RentBandCharacteristics characteristics)
    {
        var zoneNames = agreement.Bands.Select(b => b.ZoneName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = agreement.Bands.AsEnumerable();

        if (zoneNames.Count > 1)
        {
            if (string.IsNullOrWhiteSpace(characteristics.ZoneName) && string.IsNullOrWhiteSpace(characteristics.CadastralSheet))
                return null;

            candidates = candidates.Where(b => MatchesProvidedZoneAndSheet(b, characteristics));
        }
        else if (!string.IsNullOrWhiteSpace(characteristics.ZoneName) || !string.IsNullOrWhiteSpace(characteristics.CadastralSheet))
        {
            var filtered = candidates.Where(b => MatchesProvidedZoneAndSheet(b, characteristics)).ToList();
            if (filtered.Count > 0)
                candidates = filtered;
        }

        return candidates
            .Where(b => characteristics.Sqm >= b.MinSqm && (b.MaxSqm is null || characteristics.Sqm <= b.MaxSqm))
            .OrderBy(b => b.MinSqm)
            .FirstOrDefault();
    }

    private static bool MatchesProvidedZoneAndSheet(ConcordatoRentBand band, RentBandCharacteristics characteristics)
    {
        var zoneProvided = !string.IsNullOrWhiteSpace(characteristics.ZoneName);
        var sheetProvided = !string.IsNullOrWhiteSpace(characteristics.CadastralSheet);

        if (zoneProvided &&
            !string.Equals(band.ZoneName, characteristics.ZoneName!.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;

        if (!sheetProvided)
            return true;

        if (string.IsNullOrWhiteSpace(band.CadastralSheets))
            return false;

        var sheet = characteristics.CadastralSheet!.Trim();
        return band.CadastralSheets
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(s => string.Equals(s, sheet, StringComparison.OrdinalIgnoreCase));
    }

    private static int DetermineSubFascia(TerritorialRentAgreement agreement, RentBandCharacteristics characteristics)
    {
        var allA = characteristics.TypeAElementCount >= agreement.RequiredTypeACount;
        if (!allA)
            return 1;

        var fascia2 = characteristics.TypeBElementCount >= 3;
        if (!fascia2)
            return 1;

        var fascia3 = characteristics.TypeCElementCount >= 3 && characteristics.TypeDElementCount >= 2;
        return fascia3 ? 3 : 2;
    }

    private static (decimal Min, decimal Max) RatesFor(ConcordatoRentBand band, int subFascia) =>
        subFascia switch
        {
            2 => (band.SubFascia2MinEurSqmYear, band.SubFascia2MaxEurSqmYear),
            3 => (band.SubFascia3MinEurSqmYear, band.SubFascia3MaxEurSqmYear),
            _ => (band.SubFascia1MinEurSqmYear, band.SubFascia1MaxEurSqmYear),
        };

    private static decimal CoefficientFactor(TerritorialRentAgreement agreement, RentBandCharacteristics characteristics)
    {
        decimal factor = 1m;
        if (characteristics.IsFurnished)
            factor += agreement.FurnishedUpliftPercent / 100m;

        if (characteristics.Sqm < agreement.SmallSqmMax && agreement.SmallSqmMax > 0)
            factor += agreement.SmallSqmUpliftPercent / 100m;
        else if (agreement.MidSqmMin > 0
                 && characteristics.Sqm >= agreement.MidSqmMin
                 && characteristics.Sqm <= agreement.MidSqmMax)
            factor += agreement.MidSqmUpliftPercent / 100m;
        else if (agreement.LargeSqmMin > 0 && characteristics.Sqm > agreement.LargeSqmMin)
            factor -= agreement.LargeSqmReductionPercent / 100m;

        factor += characteristics.ContractYears switch
        {
            4 => agreement.Duration4UpliftPercent / 100m,
            5 => agreement.Duration5UpliftPercent / 100m,
            6 => agreement.Duration6UpliftPercent / 100m,
            _ => 0m,
        };

        return factor;
    }

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
