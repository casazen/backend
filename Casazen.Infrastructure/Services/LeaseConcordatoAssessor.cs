using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

/// <summary>What <see cref="LeaseConcordatoAssessor"/> found out.</summary>
public enum ConcordatoAssessmentKind
{
    /// <summary>The property is not visible to the caller (tenant filter) or does not exist.</summary>
    PropertyNotFound,

    /// <summary>The calculator gave no range: <see cref="ConcordatoAssessmentResult.Reason"/> says why (null: no particular reason).</summary>
    Unavailable,

    /// <summary>A range exists: <see cref="ConcordatoAssessmentResult.Assessment"/> holds it and whether the rent is inside it.</summary>
    Assessed,
}

/// <summary>
/// The result of the check of a rent against the territorial agreement: the assessment ready to be stored with the lease when there
/// is a range, whether the rent is inside it, and whether the range is only indicative.
/// </summary>
public sealed record ConcordatoAssessmentResult(
    ConcordatoAssessmentKind Kind,
    string? Reason = null,
    LeaseConcordatoAssessment? Assessment = null,
    bool Indicative = false)
{
    /// <summary>The annual rent is inside the range (only meaningful when <see cref="Kind"/> is <see cref="ConcordatoAssessmentKind.Assessed"/>).</summary>
    public bool RentWithinRange => Assessment?.RentWithinRange ?? false;

    public decimal? MinMonthly => Assessment?.CanoneMinMensile;

    public decimal? MaxMonthly => Assessment?.CanoneMaxMensile;
}

/// <summary>
/// The canone concordato range recomputed on the server from the declared characteristics and the real term (A7-12), the one rule
/// that the complete creation of a lease (<see cref="LeaseWorkflowService"/>) and the guided draft (<see cref="LeaseDraftService"/>)
/// share (LR-12: "si riusa, non si riscrive"). It decides nothing: the caller turns the result into a 422 (creation) or into a need
/// of the step (draft).
/// </summary>
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

        // Indicative: the range comes from agreement data not confirmed by a lawyer (the calculator's own flag): a guide, it never blocks (A7-23).
        return new ConcordatoAssessmentResult(ConcordatoAssessmentKind.Assessed, Assessment: assessment, Indicative: range.Indicative);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
