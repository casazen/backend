using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;

namespace Casazen.Core.Leases;

/// <summary>What the tax advisory needs to know about a lease (LT-08).</summary>
/// <param name="Term">Term from the lease dates; null when the dates are inconsistent.</param>
public sealed record LeaseTaxFacts(
    FiscalRegime LegacyRegime,
    LeaseContractType ContractType,
    LeaseTaxRegime? TaxRegime,
    decimal MonthlyRent,
    LeaseTerm? Term,
    HighTensionAreaStatus Ata,
    bool HasExtraEuTenant);

/// <summary>
/// Long-term lease tax advisory (LT-08, A7-09): cedolare secca against the ordinary regime, with the rules verified by
/// RS-5 in <c>.claude/context/regulations/fiscale.md</c> ("Conseguenze per LT-04 / LT-08") and every number from
/// <see cref="CedolareAdvisoryOptions"/>. Pure: the caller loads the lease and the ATA status.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Cedolare: the reduced rate only for a canone concordato lease in a verified ATA comune, otherwise the standard
/// rate (L11). No registration tax and no stamp duty (L4, L10).</item>
/// <item>Registration tax, first annuity: <c>max(minimum, annual rent × (concordato and verified ATA ? base share : 1) ×
/// rate)</c> (L5, L6, L8).</item>
/// <item>Stamp duty: per unit every N pages and in any case every M lines, for each copy (L10); without pages and copies
/// only the rule.</item>
/// <item>IRPEF: additional gross tax with the brackets of the configured year on the rent reduced by the flat reduction
/// (C11, C12), only with the landlord's other income; surcharges and deductions are not documented and not included.</item>
/// </list>
/// Money is rounded to the cent, half away from zero.
/// </remarks>
public static class LeaseTaxAdvisory
{
    private const int MonthsPerYear = 12;

    public static CedolareAdvisoryResult Compute(
        LeaseTaxFacts facts,
        CedolareAdvisoryInput input,
        CedolareAdvisoryOptions options,
        int currentTaxYear)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);

        var annualRent = Money(facts.MonthlyRent * MonthsPerYear);
        var concordato = facts.ContractType == LeaseContractType.Concordato;
        var reliefs = concordato && HighTensionArea.ReliefsApply(facts.Ata);

        return new CedolareAdvisoryResult(
            facts.LegacyRegime,
            facts.ContractType,
            facts.TaxRegime,
            annualRent,
            facts.Ata,
            reliefs,
            Cedolare(annualRent, reliefs, options.Cedolare),
            new OrdinaryRegimeEstimate(
                Registro(annualRent, reliefs, options.Registro),
                Bollo(input, options.Bollo),
                Irpef(annualRent, reliefs, input.OtherTaxableIncomeEur, options.Irpef, currentTaxYear)),
            Notes(facts, concordato));
    }

    private static CedolareEstimate Cedolare(decimal annualRent, bool reliefs, CedolareRateOptions cfg)
    {
        var rate = reliefs ? cfg.ConcordatoAtaRate : cfg.StandardRate;
        return new CedolareEstimate(
            rate,
            reliefs ? CedolareRateBasis.ConcordatoAta : CedolareRateBasis.Standard,
            Money(annualRent * rate),
            RegistroEur: 0m,
            BolloEur: 0m,
            cfg.Source);
    }

    private static RegistroEstimate Registro(decimal annualRent, bool reliefs, RegistroTaxOptions cfg)
    {
        var baseShare = reliefs ? cfg.ConcordatoAtaBaseShare : 1m;
        var taxableBase = Money(annualRent * baseShare);
        var computed = Money(taxableBase * cfg.Rate);
        var minimumApplied = computed < cfg.FirstYearMinimumEur;
        return new RegistroEstimate(
            cfg.Rate,
            baseShare,
            taxableBase,
            computed,
            cfg.FirstYearMinimumEur,
            minimumApplied,
            minimumApplied ? cfg.FirstYearMinimumEur : computed,
            cfg.Source);
    }

    private static BolloEstimate Bollo(CedolareAdvisoryInput input, StampDutyOptions cfg)
    {
        if (input.WrittenPages is not > 0 || input.Copies is not > 0)
        {
            return new BolloEstimate(
                AdvisoryEstimateStatus.InputRequired, cfg.EurPerUnit, cfg.PagesPerUnit, cfg.LinesPerUnit,
                Units: null, Copies: null, AmountEur: null, LinesConsidered: false, cfg.Source);
        }

        var linesConsidered = input.Lines is > 0;
        var units = Math.Max(
            CeilDiv(input.WrittenPages.Value, cfg.PagesPerUnit),
            linesConsidered ? CeilDiv(input.Lines!.Value, cfg.LinesPerUnit) : 0);
        var copies = input.Copies.Value;
        return new BolloEstimate(
            AdvisoryEstimateStatus.Computed, cfg.EurPerUnit, cfg.PagesPerUnit, cfg.LinesPerUnit,
            units, copies, Money(cfg.EurPerUnit * units * copies), linesConsidered, cfg.Source);
    }

    private static IrpefEstimate Irpef(
        decimal annualRent, bool reliefs, decimal? otherIncome, IrpefComparisonOptions cfg, int currentTaxYear)
    {
        if (!cfg.Configured)
            return NotComputed(IrpefNotComputedReasons.NotConfigured, cfg, taxableRent: null);

        var taxableRent = Money(annualRent * (1m - cfg.RentFlatReduction));
        if (cfg.TaxYear < currentTaxYear)
            return NotComputed(IrpefNotComputedReasons.BracketsOutdated, cfg, taxableRent);

        // The IRPEF relief of the canone concordato in an ATA comune is not among the verified rules (fiscale.md): no
        // figure rather than one that ignores it.
        if (reliefs)
            return NotComputed(IrpefNotComputedReasons.ConcordatoAtaReliefNotVerified, cfg, taxableRent);

        if (otherIncome is not { } other || other < 0)
        {
            return new IrpefEstimate(
                AdvisoryEstimateStatus.InputRequired, null, cfg.TaxYear, cfg.RentFlatReduction, taxableRent, null, cfg.Source);
        }

        if (other + taxableRent > cfg.ComputationIncomeLimitEur)
            return NotComputed(IrpefNotComputedReasons.IncomeOverLimit, cfg, taxableRent);

        var additional = Money(GrossTax(other + taxableRent, cfg.Brackets) - GrossTax(other, cfg.Brackets));
        return new IrpefEstimate(
            AdvisoryEstimateStatus.Computed, null, cfg.TaxYear, cfg.RentFlatReduction, taxableRent, additional, cfg.Source);
    }

    private static IrpefEstimate NotComputed(string reason, IrpefComparisonOptions cfg, decimal? taxableRent) =>
        new(AdvisoryEstimateStatus.NotComputed, reason, cfg.Configured ? cfg.TaxYear : null,
            cfg.Configured ? cfg.RentFlatReduction : null, taxableRent, null, cfg.Configured ? cfg.Source : null);

    /// <summary>Gross tax of <paramref name="income"/> with ascending brackets (the last one open).</summary>
    public static decimal GrossTax(decimal income, IReadOnlyList<IrpefBracketOptions> brackets)
    {
        decimal tax = 0, lower = 0;
        foreach (var bracket in brackets)
        {
            if (income <= lower)
                break;

            var upper = bracket.UpToEur is { } upTo ? Math.Min(income, upTo) : income;
            tax += (upper - lower) * bracket.Rate;
            lower = bracket.UpToEur ?? income;
        }

        return tax;
    }

    private static List<string> Notes(LeaseTaxFacts facts, bool concordato)
    {
        var notes = new List<string>();
        if (concordato)
        {
            switch (facts.Ata)
            {
                case HighTensionAreaStatus.Unverified:
                    notes.Add(LeaseTaxAdvisoryNoteCodes.AtaUnverified);
                    notes.Add(LeaseTaxAdvisoryNoteCodes.EmergencyComuniNotChecked);
                    break;
                case HighTensionAreaStatus.NotListed:
                    notes.Add(LeaseTaxAdvisoryNoteCodes.AtaNotListed);
                    notes.Add(LeaseTaxAdvisoryNoteCodes.EmergencyComuniNotChecked);
                    break;
            }

            notes.Add(LeaseTaxAdvisoryNoteCodes.ConcordatoAttestationRequired);
            if (facts.TaxRegime is null)
                notes.Add(LeaseTaxAdvisoryNoteCodes.TaxRegimeUnknown);
        }

        if (facts.ContractType == LeaseContractType.Transitorio)
            notes.Add(LeaseTaxAdvisoryNoteCodes.TransitorioReliefsToConfirm);

        if (facts.Term is { } term && term.Months < MonthsPerYear)
            notes.Add(LeaseTaxAdvisoryNoteCodes.ShortTermAnnualized);

        if (facts.HasExtraEuTenant)
            notes.Add(LeaseTaxAdvisoryNoteCodes.QuesturaNotReplaced);

        return notes;
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    private static decimal Money(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
