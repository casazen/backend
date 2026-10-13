using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// The saved views of the lease list (LR-01, B3, gap report 04 § 4.1): "Attivi", "In preparazione", "In scadenza",
/// "Terminati" and "Tutti". They are not stored: each is derived on read from the status of the lease and its end date
/// (<see cref="LeaseListViews.Predicate"/>), because the real model has no "in scadenza" nor "terminato" status.
/// </summary>
public enum LeaseListView
{
    /// <summary>Every lease (the list as it has always been; the default).</summary>
    All = 0,

    /// <summary>Registered and not ended yet. Includes the leases that are about to expire.</summary>
    Active = 1,

    /// <summary>Not registered yet: draft, in signature, signed and waiting for the registration (or the registration in progress).</summary>
    InPreparation = 2,

    /// <summary>Registered, not ended, and ending within <see cref="LeaseListViews.ExpiringWithinMonths"/> months. A subset of <see cref="Active"/>.</summary>
    Expiring = 3,

    /// <summary>Registered and ended, or rejected (a contract that will not take effect).</summary>
    Ended = 4,
}

/// <summary>What the lease list is asked for: a property, a view and a text to look for (property or tenant).</summary>
public sealed record LeaseListQuery(Guid? PropertyId = null, LeaseListView View = LeaseListView.All, string? Search = null)
{
    /// <summary>Longest search text taken into account (a longer one is cut).</summary>
    public const int MaxSearchLength = 100;

    /// <summary>The search text trimmed and cut, or <c>null</c> when there is nothing to look for.</summary>
    public string? NormalizedSearch
    {
        get
        {
            var text = Search?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;
            return text.Length > MaxSearchLength ? text[..MaxSearchLength].TrimEnd() : text;
        }
    }
}

/// <summary>
/// The rule behind each <see cref="LeaseListView"/>, written once as a SQL expression. The calendar day is the Europe/Rome
/// "today" as midnight UTC (<c>RomeCalendar.TodayInRome</c>), the same as the dates of the leases (storage convention).
/// </summary>
/// <remarks>
/// <para>A lease has <b>ended</b> from the day after its last day (<see cref="LeaseContract.EndDate"/>), the same rule as the
/// retention of the parties' data (<c>LeasePartyPrivacyService.HasEnded</c>). A lease that was never registered stays "in
/// preparazione" whatever its dates: a draft or a signed contract still to be registered is work to finish, not a closed
/// lease, and there is no way to delete a draft yet.</para>
/// <para>"In scadenza" starts six months before the end date (the notice period of the 4+4 and 3+2 contracts is the
/// same six months, <see cref="LongRentDeadlineRules.NoticeMonthsBeforeEnd"/>); a transitory lease, which has no renewal,
/// is "in scadenza" for its last six months as well.</para>
/// </remarks>
public static class LeaseListViews
{
    /// <summary>Months before the end date from which an active lease is "in scadenza".</summary>
    public const int ExpiringWithinMonths = LongRentDeadlineRules.NoticeMonthsBeforeEnd;

    public static Expression<Func<LeaseContract, bool>> Predicate(LeaseListView view, DateTime todayInRome)
    {
        var expiringUntil = todayInRome.AddMonths(ExpiringWithinMonths);
        return view switch
        {
            LeaseListView.Active => l => l.Status == LeaseStatus.Registered && l.EndDate >= todayInRome,
            LeaseListView.Expiring => l => l.Status == LeaseStatus.Registered && l.EndDate >= todayInRome && l.EndDate <= expiringUntil,
            LeaseListView.Ended => l => (l.Status == LeaseStatus.Registered && l.EndDate < todayInRome) || l.Status == LeaseStatus.Rejected,
            LeaseListView.InPreparation => l => l.Status != LeaseStatus.Registered && l.Status != LeaseStatus.Rejected,
            LeaseListView.All => l => true,
            _ => throw new ArgumentOutOfRangeException(nameof(view), view, "Unknown lease list view."),
        };
    }
}
