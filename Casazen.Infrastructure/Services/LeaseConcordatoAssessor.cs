using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

public enum ConcordatoAssessmentKind
{
    PropertyNotFound,

    Unavailable,

    Assessed,
}

public sealed record ConcordatoAssessmentResult(
    ConcordatoAssessmentKind Kind,
    string? Reason = null,
    LeaseConcordatoAssessment? Assessment = null,
    bool Indicative = false)
{
    public bool RentWithinRange => Assessment?.RentWithinRange ?? false;

    public decimal? MinMonthly => Assessment?.CanoneMinMensile;

    public decimal? MaxMonthly => Assessment?.CanoneMaxMensile;
}

internal sealed class LeaseConcordatoAssessor(ICanoneConcordatoEligibilityService eligibility, TimeProvider clock)
{
    public async Task<ConcordatoAssessmentResult> AssessAsync(
        Guid propertyId,
        RentBandCharacteristics characteristics,
        LeaseTerm term,
        decimal monthlyRent,
        CancellationToken cancellationToken = default)
    {
        var range = await eligibility.CalculateAsync(propertyId, characteristics, term, cancellationToken);
        if (range is null)
            return new ConcordatoAssessmentResult(ConcordatoAssessmentKind.PropertyNotFound);

        if (range is not
            {
                Available: true,
                SubFascia: int subFascia,
                CanoneMinAnnuo: decimal minAnnual,
                CanoneMaxAnnuo: decimal maxAnnual,
                CanoneMinMensile: decimal minMonthly,
                CanoneMaxMensile: decimal maxMonthly,
            })
        {
            return new ConcordatoAssessmentResult(ConcordatoAssessmentKind.Unavailable, range.ReasonCode);
        }

        var annualRent = monthlyRent * 12m;
        var withinRange = annualRent >= minAnnual && annualRent <= maxAnnual;

        var assessment = new LeaseConcordatoAssessment
        {
            Sqm = characteristics.Sqm,
            GarageSqm = characteristics.GarageSqm,
            BalconySqm = characteristics.BalconySqm,
            OtherAppurtenanceSqm = characteristics.OtherAppurtenanceSqm,
            PrivateGreenSqm = characteristics.PrivateGreenSqm,
            TypeAElementCount = characteristics.TypeAElementCount,
            TypeBElementCount = characteristics.TypeBElementCount,
            TypeCElementCount = characteristics.TypeCElementCount,
            TypeDElementCount = characteristics.TypeDElementCount,
            QualifyingTypeDElementCount = characteristics.QualifyingTypeDElementCount,
            StoveHeating = characteristics.StoveHeating,
            IsFurnished = characteristics.IsFurnished,
            AirConditioning = characteristics.AirConditioning,
            ZoneName = NullIfBlank(characteristics.ZoneName),
            CadastralSheet = NullIfBlank(characteristics.CadastralSheet),
            ContractYears = range.ContractYears ?? term.Months / 12,
            UsableSqm = range.UsableSqm ?? characteristics.Sqm,
            Zone = range.Zone ?? string.Empty,
            SubFascia = subFascia,
            CanoneMinAnnuo = minAnnual,
            CanoneMaxAnnuo = maxAnnual,
            CanoneMinMensile = minMonthly,
            CanoneMaxMensile = maxMonthly,
            DataCompleteness = range.DataCompleteness ?? DataCompleteness.Missing,
            RentWithinRange = withinRange,
            CalculatedAt = clock.GetUtcNow().UtcDateTime,
        };

        return new ConcordatoAssessmentResult(ConcordatoAssessmentKind.Assessed, Assessment: assessment, Indicative: range.Indicative);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
