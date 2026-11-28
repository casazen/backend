using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

/// <summary>
/// The figures of the long-term area that the backend applies and the interface must not copy (LR-12, decision LR-D29: no
/// figure of law written in the frontend). One place: the draft of a lease checks against these (<see cref="LeaseDraftProgress"/>)
/// and <c>GET api/long-rent/rules</c> hands them to the screens, so a text like "deposito al massimo 3 mensilità" is built from the
/// answer and cannot drift from the check.
/// </summary>
/// <remarks>
/// The terms, the notice and the registration deadline are the constants the rest of the backend already uses
/// (<see cref="LeaseContractTerms"/>, <see cref="LongRentDeadlineRules"/>, <see cref="RliRegistrationDeadline"/>), read here, not
/// copied. The deposit rule and the renewal period are product values taken from the demo (<c>LAW</c> of the prototype) and the
/// decision of the interview (D5), <b>not verified</b> by a lawyer: LR-D29 makes that verification a condition for turning the area
/// on, and changing a figure is a change here and nowhere else.
/// </remarks>
public static class LongRentRules
{
    public const int DepositMaxRents = 3;

    public const int LiberoRenewalMonths = 48;

    public const int ConcordatoRenewalMonths = 24;

    /// <summary>
    /// The types of contract that can be created in CasaZen (decision D27): the libero, the concordato and, since TR, the transitory one,
    /// whose model is the official Allegato B of the D.M. 16/01/2017 (<c>Casazen.Web/LeaseTemplates/Transitorio</c>, still to be approved by
    /// the product owner). The student lease is shown in the wizard but blocked ("non ancora disponibile").
    /// </summary>
    public static IReadOnlyList<LeaseContractType> AvailableTypes { get; } =
        [LeaseContractType.Libero, LeaseContractType.Concordato, LeaseContractType.Transitorio];

    public static IReadOnlyList<LeaseContractType> ImportedTypes { get; } =
        [LeaseContractType.Libero, LeaseContractType.Concordato, LeaseContractType.Transitorio];

    public static bool IsAvailable(LeaseContractType type) => AvailableTypes.Contains(type);

    public static bool IsAvailable(LeaseContractType type, LeaseOrigin origin) =>
        origin == LeaseOrigin.Imported ? ImportedTypes.Contains(type) : IsAvailable(type);

    public static int? FirstTermMonths(LeaseContractType type) => type switch
    {
        LeaseContractType.Libero => LeaseContractTerms.LiberoMinimumMonths,
        LeaseContractType.Concordato => LeaseContractTerms.ConcordatoMinimumMonths,
        _ => null,
    };

    public static int? RenewalMonths(LeaseContractType type) => type switch
    {
        LeaseContractType.Libero => LiberoRenewalMonths,
        LeaseContractType.Concordato => ConcordatoRenewalMonths,
        _ => null,
    };

    /// <summary>
    /// The last day (included) of a first period of <paramref name="months"/> months that starts on <paramref name="startDate"/>:
    /// "from 1 November 2026 for 48 months" ends on 31 October 2030. The term between the two dates is exactly
    /// <paramref name="months"/> months for every start date (<see cref="LeaseTerm.Between"/>), so the end date always passes
    /// <see cref="LeaseContractTerms.EnsureTerm"/>.
    /// </summary>
    public static DateTime FirstTermEnd(DateTime startDate, int months) => startDate.Date.AddMonths(months).AddDays(-1);

    public static LongRentRulesDto Current { get; } = new(
        TermMonths: new Dictionary<string, int>
        {
            [nameof(LeaseContractType.Libero)] = LeaseContractTerms.LiberoMinimumMonths,
            [nameof(LeaseContractType.Concordato)] = LeaseContractTerms.ConcordatoMinimumMonths,
        },
        RenewalMonths: new Dictionary<string, int>
        {
            [nameof(LeaseContractType.Libero)] = LiberoRenewalMonths,
            [nameof(LeaseContractType.Concordato)] = ConcordatoRenewalMonths,
        },
        NoticeMonths: LongRentDeadlineRules.NoticeMonthsBeforeEnd,
        RegistrationDays: RliRegistrationDeadline.DaysFromStipulaOrStart,
        DepositMaxRents: DepositMaxRents,
        AvailableTypes: AvailableTypes.Select(t => t.ToString()).ToList(),
        ImportedTypes: ImportedTypes.Select(t => t.ToString()).ToList(),
        TransitoryTermMonths: new LongRentMonthRange(LeaseContractTerms.TransitorioMinimumMonths, LeaseContractTerms.TransitorioMaximumMonths),
        TransitoryDocumentationAfterDays: LeaseContractTerms.TransitorioDocumentationAfterDays,
        TransitoryBandFromInhabitants: LeaseContractTerms.TransitorioBandFromInhabitants);
}

/// <summary>
/// The answer of <c>GET api/long-rent/rules</c>: the terms of the first period and of the renewal per contract type, the months
/// of notice, the days to register, the deposit limit and the types that can be created. The keys of the two dictionaries and the
/// values of <c>AvailableTypes</c> and <c>ImportedTypes</c> are the names of <see cref="LeaseContractType"/>. Since LR-13 two
/// fields (at the end) tell which types an imported contract can have and the term, free inside it, of the transitory one; since TR
/// the last two are the figures of the transitory contract the screens need: after how many days of term its need must be documented
/// and above how many inhabitants the rent has to be inside the bands of the territorial agreement.
/// </summary>
public sealed record LongRentRulesDto(
    IReadOnlyDictionary<string, int> TermMonths,
    IReadOnlyDictionary<string, int> RenewalMonths,
    int NoticeMonths,
    int RegistrationDays,
    int DepositMaxRents,
    IReadOnlyList<string> AvailableTypes,
    IReadOnlyList<string> ImportedTypes,
    LongRentMonthRange TransitoryTermMonths,
    int TransitoryDocumentationAfterDays,
    int TransitoryBandFromInhabitants);

public sealed record LongRentMonthRange(int Min, int Max);
