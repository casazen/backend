using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Leases;

/// <summary>
/// What the agenda of the long-term area lists (LR-01, B2). Stored as the name, in the JSON of the API.
/// <list type="bullet">
/// <item><see cref="RliRegistration"/>: the day by which a lease still to be registered must be (LT-04).</item>
/// <item><see cref="Questura"/>: the 48 hours of the communication for an extra-EU tenant, not declared yet (LT-07).</item>
/// <item><see cref="LeaseEnd"/>: the last day of a registered lease.</item>
/// <item><see cref="Notice"/>: the last day to give notice, six months before the end (4+4 and 3+2 contracts).</item>
/// <item><see cref="Rent"/>: the due date of a rent installment not paid yet.</item>
/// </list>
/// IMU is not listed: the data per comune (<c>ComuneImuChannel</c>) carries no deadline. ISTAT is out of the first release
/// (decision D8).
/// </summary>
public enum LongRentDeadlineType
{
    RliRegistration = 0,
    Questura = 1,
    LeaseEnd = 2,
    Notice = 3,
    Rent = 4,
}

/// <summary>Rules and limits of the agenda of the long-term area (LR-01, B2).</summary>
public static class LongRentDeadlineRules
{
    /// <summary>
    /// Months before the end of the contract by which notice (disdetta or refusal to renew) must be given: the 6 months of
    /// the 4+4 and 3+2 contracts (L. 431/1998), as in the task LR-01 and the demo (<c>LAW.noticeMonths</c>). It is the
    /// product value of the wave spec, not a figure verified in <c>.claude/context/regulations</c>: to be confirmed by the
    /// lawyer together with the contract texts (decision D1 of the gap report 04).
    /// </summary>
    public const int NoticeMonthsBeforeEnd = 6;

    /// <summary>Days shown when the caller gives no <c>to</c>: from <c>from</c> (today by default) to this many days later.</summary>
    public const int DefaultWindowDays = 90;

    /// <summary>Longest window the agenda answers for (a year and a leap day), so a request cannot ask for years of installments.</summary>
    public const int MaxWindowDays = 366;

    /// <summary>Installments listed at most in one answer (the earliest by due date); beyond it the answer says it was cut.</summary>
    public const int MaxRentItems = 500;

    /// <summary>
    /// The last day to give notice of a contract that ends on <paramref name="endDate"/>, or <c>null</c> when the contract
    /// type has no notice: a transitory lease ends by itself (D.M. 16/01/2017 art. 2), with no renewal to refuse. The end
    /// date is the one entered for the contract (the first expiry): renewals are not modelled (D8).
    /// </summary>
    public static DateOnly? NoticeDate(LeaseContractType contractType, DateTime endDate) =>
        contractType is LeaseContractType.Libero or LeaseContractType.Concordato
            ? RomeCalendar.DateInRome(endDate).AddMonths(-NoticeMonthsBeforeEnd)
            : null;
}
