using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;

namespace Casazen.Core.Leases;

/// <summary>Stable codes of the lease term rules (LT-10, A7-13), answered as 422.</summary>
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
    /// Legacy combined value (template, IMU notice, cedolare advisory): a concordato lease is always
    /// <see cref="FiscalRegime.CanoneConcordato"/>; the other types follow the tax regime.
    /// </summary>
    public static FiscalRegime LegacyFiscalRegime(LeaseContractType type, LeaseTaxRegime? taxRegime) => type switch
    {
        LeaseContractType.Concordato => FiscalRegime.CanoneConcordato,
        _ => taxRegime == LeaseTaxRegime.Ordinario ? FiscalRegime.RegimeOrdinario : FiscalRegime.CedolareSecca,
    };

    /// <summary>
    /// Contract type and tax regime of a request that sends only the legacy value. A concordato lease had no tax regime:
    /// it stays unknown (null), never guessed.
    /// </summary>
    public static (LeaseContractType Type, LeaseTaxRegime? TaxRegime) FromLegacy(FiscalRegime regime) => regime switch
    {
        FiscalRegime.CanoneConcordato => (LeaseContractType.Concordato, null),
        FiscalRegime.RegimeOrdinario => (LeaseContractType.Libero, LeaseTaxRegime.Ordinario),
        _ => (LeaseContractType.Libero, LeaseTaxRegime.CedolareSecca),
    };

    /// <summary>
    /// Throws a <see cref="DomainRuleException"/> (422) when the term from <paramref name="startDate"/> to the inclusive
    /// <paramref name="endDate"/> is not allowed for <paramref name="type"/>.
    /// </summary>
    public static void EnsureTerm(LeaseContractType type, DateTime startDate, DateTime endDate)
    {
        if (CheckTerm(type, startDate, endDate) is { } problem)
            throw new DomainRuleException(problem.Code, problem.MessageKey);
    }

    /// <summary>
    /// The check of <see cref="EnsureTerm"/> without the exception (LR-13: the guided draft of an imported contract reports the problem
    /// as something to fix, it does not refuse the request): <c>null</c> when the term is allowed. Without a type only the order of the
    /// dates is checked.
    /// </summary>
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

    /// <summary>True when <paramref name="term"/> is longer than exactly <paramref name="months"/> months.</summary>
    public static bool IsLongerThan(LeaseTerm term, int months) =>
        term.Months > months || (term.Months == months && term.Days > 0);
}

/// <summary>
/// What is wrong with a term (<see cref="LeaseContractTerms.CheckTerm"/>): the code and the message key of the 422 it would be, and the
/// limits of the type in whole months (<c>MaxMonths</c> only where the type has one).
/// </summary>
public sealed record LeaseTermProblem(string Code, string MessageKey, int? MinMonths, int? MaxMonths);

/// <summary>Stable codes of the canone concordato checks at lease creation (LT-10, A7-12), answered as 422.</summary>
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

    /// <summary>Problem code and message key for a range the calculator could not give (its reason code).</summary>
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
