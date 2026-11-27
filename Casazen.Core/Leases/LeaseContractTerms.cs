using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Leases;

public static class LeaseTermErrorCodes
{
    public const string EndBeforeStart = "lease_end_before_start";
    public const string TooShort = "lease_term_too_short";
    public const string TooLong = "lease_term_too_long";
    public const string ContractTypeRequired = "lease_contract_type_required";
    public const string TaxRegimeRequired = "lease_tax_regime_required";
}

/// <summary>
/// Term rules of each contract type and the mapping to the legacy <see cref="FiscalRegime"/> (LT-10, A7-13). The term is
/// computed from the dates (<see cref="LeaseTerm"/>, end date inclusive), never taken from the client.
/// </summary>
/// <remarks>
/// Sources (<c>.claude/context/regulations/canone_concordato.md</c>): canone libero 4+4 (L. 431/1998 art. 2 c. 1); canone
/// concordato "durata [...] superiore alla minima triennale" (art. 2 c. 3, MB agreement p. 9, class U); transitorio "fino
/// a 18 mesi" (D.M. 16/01/2017 art. 2, class T) with a minimum of one month from the same article. The transitory needs
/// that must be stated in the contract are listed only in each territorial agreement and are not modelled.
/// </remarks>
public static class LeaseContractTerms
{
    public const int LiberoMinimumMonths = 48;
    public const int ConcordatoMinimumMonths = 36;
    public const int TransitorioMinimumMonths = 1;
    public const int TransitorioMaximumMonths = 18;

    /// <summary>
    /// A transitory contract that lasts more than this many days must prove its need with documentation attached to the contract
    /// (D.M. 16/01/2017 art. 2 c. 4); one of 30 days or less is free of that and of the band of the rent (art. 2 c. 2).
    /// </summary>
    public const int TransitorioDocumentationAfterDays = 30;

    /// <summary>
    /// In the municipalities with more than this many inhabitants the rent of a transitory contract is defined inside the bands of the
    /// territorial agreement (D.M. 16/01/2017 art. 2 c. 2). CasaZen does not check it (TR-FU1): the review only tells the landlord.
    /// </summary>
    public const int TransitorioBandFromInhabitants = 10_000;

    /// <summary>
    /// True when a transitory contract from <paramref name="startDate"/> to the inclusive <paramref name="endDate"/> lasts more than
    /// <see cref="TransitorioDocumentationAfterDays"/> days, so its need must be documented (art. 2 c. 4). An end before the start lasts
    /// no days.
    /// </summary>
    public static bool TransitoryNeedsDocumentation(DateTime startDate, DateTime endDate) =>
        (endDate.Date - startDate.Date).Days + 1 > TransitorioDocumentationAfterDays;

    /// <summary>
    /// Legacy combined value (template, IMU notice, cedolare advisory): a concordato lease is always
    /// <see cref="FiscalRegime.CanoneConcordato"/>; the other types follow the tax regime.
    /// </summary>
    public static FiscalRegime LegacyFiscalRegime(LeaseContractType type, LeaseTaxRegime? taxRegime) => type switch
    {
        LeaseContractType.Concordato => FiscalRegime.CanoneConcordato,
        _ => taxRegime == LeaseTaxRegime.Ordinario ? FiscalRegime.RegimeOrdinario : FiscalRegime.CedolareSecca,
    };

    public static (LeaseContractType Type, LeaseTaxRegime? TaxRegime) FromLegacy(FiscalRegime regime) => regime switch
    {
        FiscalRegime.CanoneConcordato => (LeaseContractType.Concordato, null),
        FiscalRegime.RegimeOrdinario => (LeaseContractType.Libero, LeaseTaxRegime.Ordinario),
        _ => (LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca),
    };

    public static void EnsureTerm(LeaseContractType type, DateTime startDate, DateTime endDate)
    {
        if (CheckTerm(type, startDate, endDate) is { } problem)
            throw new DomainRuleException(problem.Code, problem.MessageKey);
    }

    public static LeaseTermProblem? CheckTerm(LeaseContractType? type, DateTime startDate, DateTime endDate)
    {
        if (LeaseTerm.Between(startDate, endDate) is not { } term || endDate.Date <= startDate.Date)
            return new LeaseTermProblem(LeaseTermErrorCodes.EndBeforeStart, "LeaseEndBeforeStart", null, null);

        return type switch
        {
            LeaseContractType.Libero when term.Months < LiberoMinimumMonths =>
                new LeaseTermProblem(LeaseTermErrorCodes.TooShort, "LeaseTermTooShortLibero", LiberoMinimumMonths, null),
            LeaseContractType.Concordato when term.Months < ConcordatoMinimumMonths =>
                new LeaseTermProblem(LeaseTermErrorCodes.TooShort, "LeaseTermTooShortConcordato", ConcordatoMinimumMonths, null),
            LeaseContractType.Transitorio when term.Months < TransitorioMinimumMonths =>
                new LeaseTermProblem(LeaseTermErrorCodes.TooShort, "LeaseTermOutOfRangeTransitorio", TransitorioMinimumMonths, TransitorioMaximumMonths),
            LeaseContractType.Transitorio when IsLongerThan(term, TransitorioMaximumMonths) =>
                new LeaseTermProblem(LeaseTermErrorCodes.TooLong, "LeaseTermOutOfRangeTransitorio", TransitorioMinimumMonths, TransitorioMaximumMonths),
            _ => null,
        };
    }

    public static bool IsLongerThan(LeaseTerm term, int months) =>
        term.Months > months || (term.Months == months && term.Days > 0);
}

public sealed record LeaseTermProblem(string Code, string MessageKey, int? MinMonths, int? MaxMonths);

public static class ConcordatoErrorCodes
{
    public const string CharacteristicsRequired = "concordato_characteristics_required";
    public const string RentOutOfRange = "concordato_rent_out_of_range";
    public const string RangeUnavailable = "concordato_range_unavailable";
    public const string ZoneRequired = "concordato_zone_required";
    public const string ZoneNotFound = "concordato_zone_not_found";
    public const string InvalidSurface = "concordato_invalid_surface";
    public const string SurfaceOutOfBands = "concordato_surface_out_of_bands";
    public const string InvalidElementCounts = "concordato_invalid_element_counts";
    public const string TermTooShort = "concordato_term_too_short";

    public static (string Code, string MessageKey) ForReason(string? reasonCode) => reasonCode switch
    {
        CanoneConcordatoReasonCodes.ZoneRequired => (ZoneRequired, "ConcordatoZoneRequired"),
        CanoneConcordatoReasonCodes.ZoneNotFound => (ZoneNotFound, "ConcordatoZoneNotFound"),
        CanoneConcordatoReasonCodes.InvalidSurface => (InvalidSurface, "ConcordatoInvalidSurface"),
        CanoneConcordatoReasonCodes.SurfaceOutOfBands => (SurfaceOutOfBands, "ConcordatoSurfaceOutOfBands"),
        CanoneConcordatoReasonCodes.InvalidElementCounts => (InvalidElementCounts, "ConcordatoInvalidElementCounts"),
        CanoneConcordatoReasonCodes.TermTooShort => (TermTooShort, "LeaseTermTooShortConcordato"),
        _ => (RangeUnavailable, "ConcordatoRangeUnavailable"),
    };
}
