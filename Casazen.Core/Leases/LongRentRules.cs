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
    /// <summary>The security deposit is at most this many monthly rents (demo <c>LAW.depositMaxRents</c>; to be confirmed, LR-D29).</summary>
    public const int DepositMaxRents = 3;

    /// <summary>Months a tacitly renewed canone libero (4+4) goes on for (interview D5).</summary>
    public const int LiberoRenewalMonths = 48;

    /// <summary>Months a tacitly renewed canone concordato (3+2) goes on for (interview D5).</summary>
    public const int ConcordatoRenewalMonths = 24;

    /// <summary>
    /// The types of contract that can be created in the first release (decision D27): the transitory and the student lease are
    /// shown in the wizard but blocked ("non ancora disponibile"); the transitory one enters only as an imported contract (LR-13).
    /// </summary>
    public static IReadOnlyList<LeaseContractType> AvailableTypes { get; } = [LeaseContractType.Libero, LeaseContractType.Concordato];

    public static bool IsAvailable(LeaseContractType type) => AvailableTypes.Contains(type);

    /// <summary>
    /// Months of the first period of a contract created in CasaZen: 48 for the libero, 36 for the concordato (the minimum of the
    /// type, <see cref="LeaseContractTerms"/>); <c>null</c> for a type that is not created here.
    /// </summary>
    public static int? FirstTermMonths(LeaseContractType type) => type switch
    {
        LeaseContractType.Libero => LeaseContractTerms.LiberoMinimumMonths,
        LeaseContractType.Concordato => LeaseContractTerms.ConcordatoMinimumMonths,
        _ => null,
    };

    /// <summary>Months of a tacit renewal; <c>null</c> for a type that does not renew (the transitory one).</summary>
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

    /// <summary>The rules as the API hands them to the screens (<c>GET api/long-rent/rules</c>).</summary>
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
        AvailableTypes: AvailableTypes.Select(t => t.ToString()).ToList());
}

/// <summary>
/// The answer of <c>GET api/long-rent/rules</c>: the terms of the first period and of the renewal per contract type, the months
/// of notice, the days to register, the deposit limit and the types that can be created. The keys of the two dictionaries and the
/// values of <c>AvailableTypes</c> are the names of <see cref="LeaseContractType"/>.
/// </summary>
public sealed record LongRentRulesDto(
    IReadOnlyDictionary<string, int> TermMonths,
    IReadOnlyDictionary<string, int> RenewalMonths,
    int NoticeMonths,
    int RegistrationDays,
    int DepositMaxRents,
    IReadOnlyList<string> AvailableTypes);
