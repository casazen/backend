using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Leases;

/// <summary>
/// The tacit renewal of a long-term lease and the end of the period it is in (LR-14, decision D5 of the interview of the area): a
/// canone libero (4+4) or concordato (3+2) lease that nobody gave notice of goes on by itself for one more period, so the end the
/// contract states (<see cref="LeaseContract.EndDate"/>) is only the end of its first period and the end that matters is
/// <see cref="LeaseContract.CurrentTermEndDate"/>. A transitory lease has no renewal. The lengths of the periods and the months of notice are the
/// figures of <see cref="LongRentRules"/> and <see cref="LongRentDeadlineRules"/>, product values that a lawyer has still to confirm
/// before the area is released (decision LR-D29).
/// </summary>
/// <remarks>
/// <para><b>When a lease renews.</b> It is signed by every party (<see cref="InForceStatuses"/>), its type renews
/// (<see cref="RenewableTypes"/>), nobody gave notice (<see cref="LeaseContract.NoticeOn"/>), it was not ended early
/// (<see cref="LeaseContract.TerminatedOn"/>) and its current period is over: the last day is before today (Europe/Rome). That is
/// <see cref="DueOn"/>, written once for SQL; the job asks it, and asks it again under the lock of the row of each lease.</para>
/// <para><b>One period at a time.</b> The new last day is the day before the same day, one period later: a lease that ended on 31 October
/// 2030 and renews for 48 months ends on 31 October 2034, one that ended on 28 February 2029 on 28 February 2033, the same rule as the end of
/// a first period (<see cref="LongRentRules.FirstTermEnd"/>). A lease that is several periods behind (the job did not run for years) is
/// brought forward by at most <see cref="MaxPeriodsPerRun"/> periods in a run and the next run goes on.</para>
/// <para><b>No retroactive renewal</b> (decision of the product owner, 17 October 2026): a lease whose last day had already passed when CasaZen
/// came to know it stays <b>concluded</b> and never renews. It is recorded with <see cref="LeaseContract.TerminatedOn"/> = its last day, the
/// mark that also tells a reader it is over and not about to renew, at the three doors through which a lease comes in force: the migration
/// that introduced the renewal (the leases that had ended by then), the signature (<see cref="ConcludeIfEnded"/>: a contract imported after
/// its end, or any contract signed after its last day) and the repair at the start of each run of the job (a lease written during a deploy
/// by a server that did not know the columns yet).</para>
/// </remarks>
public static class LeaseRenewal
{
    /// <summary>
    /// The most periods one run of the job adds to one lease: a lease can only be that far behind if the job did not run for decades,
    /// and a bad date can never make a run loop for long. The next run goes on from where this one stopped.
    /// </summary>
    public const int MaxPeriodsPerRun = 20;

    /// <summary>
    /// The statuses of a lease signed by every party and not rejected: the contract is in force whether or not its registration is
    /// finished (the registration is a tax duty, not a condition of the contract). Only these can renew.
    /// </summary>
    public static IReadOnlyList<LeaseStatus> InForceStatuses { get; } =
        [LeaseStatus.Signed, LeaseStatus.RegistrationPending, LeaseStatus.SentToProvider, LeaseStatus.Registered];

    /// <summary>The types that renew by themselves: the ones <see cref="LongRentRules.RenewalMonths"/> gives a period to (libero, concordato).</summary>
    public static IReadOnlyList<LeaseContractType> RenewableTypes { get; } =
        Enum.GetValues<LeaseContractType>().Where(Renews).ToArray();

    public static bool IsInForce(LeaseStatus status) => InForceStatuses.Contains(status);

    /// <summary>True for a type that renews by itself; the transitory one never does.</summary>
    public static bool Renews(LeaseContractType type) => LongRentRules.RenewalMonths(type) is not null;

    /// <summary>
    /// The last day (included, midnight UTC) of the period that follows the one ending on <paramref name="currentEnd"/>, or
    /// <c>null</c> for a type that does not renew.
    /// </summary>
    public static DateTime? NextEnd(DateTime currentEnd, LeaseContractType type)
    {
        if (LongRentRules.RenewalMonths(type) is not { } months)
            return null;

        var firstDay = currentEnd.Date.AddDays(1);
        return DateTime.SpecifyKind(LongRentRules.FirstTermEnd(firstDay, months), DateTimeKind.Utc);
    }

    /// <summary>
    /// The leases that renew now: in force, of a renewable type, with no notice and not ended early, whose current period ended before
    /// <paramref name="todayInRome"/> (the Rome calendar day as midnight UTC). Written for SQL; <see cref="IsDue"/> is the same rule for
    /// a lease already in memory.
    /// </summary>
    public static Expression<Func<LeaseContract, bool>> DueOn(DateTime todayInRome)
    {
        var types = RenewableTypes.ToArray();
        var statuses = InForceStatuses.ToArray();
        return l => l.CurrentTermEndDate < todayInRome
                    && l.NoticeOn == null
                    && l.TerminatedOn == null
                    && types.Contains(l.ContractType)
                    && statuses.Contains(l.Status);
    }

    /// <summary><see cref="DueOn"/> for a lease already read.</summary>
    public static bool IsDue(LeaseContract lease, DateTime todayInRome)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return DueOn(todayInRome).Compile()(lease);
    }

    /// <summary>
    /// Records as <b>concluded</b> a lease of a renewable type that is signed after the last day of its period (no retroactive renewal): a
    /// contract brought into CasaZen when it was already over, typically an imported one, is not renewed by itself. It gets the mark the
    /// migration gave to the leases that had ended when the renewal was introduced, <see cref="LeaseContract.TerminatedOn"/> = its last day,
    /// so that nothing renews it and every reader tells it from a lease about to renew. Called where the lease becomes signed; a period that
    /// is not over, a type that does not renew and a lease already recorded as ended are left as they are.
    /// </summary>
    /// <param name="todayInRome">Today's Europe/Rome calendar day as midnight UTC (<c>TimeProvider.TodayInRome()</c>).</param>
    public static void ConcludeIfEnded(LeaseContract lease, DateTime todayInRome)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.TerminatedOn is null && lease.CurrentTermEndDate < todayInRome && Renews(lease.ContractType))
            lease.TerminatedOn = lease.CurrentTermEndDate;
    }

    /// <summary>
    /// True when a notice given on <paramref name="noticeOn"/> is later than the last day to give notice of the period that ends on
    /// <paramref name="currentEnd"/> (six months before it, <see cref="LongRentDeadlineRules.NoticeDate"/>): it may not stop the renewal in
    /// law. CasaZen only tells the landlord; whether a notice is valid is not for it to judge (decision LR-D29).
    /// </summary>
    public static bool IsNoticePossiblyLate(LeaseContractType type, DateTime currentEnd, DateTime noticeOn) =>
        LongRentDeadlineRules.NoticeDate(type, currentEnd) is { } lastDay && RomeCalendar.DateInRome(noticeOn) > lastDay;
}
