using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// How the landlord reads the state of a rent installment (LT-06, LR-01): the one definition of "overdue" and of "still to
/// be collected", used by the ledger of a lease (<c>RentInstallmentView.IsOverdue</c>), the rent register of the area, the
/// lease list and the overview. The expression forms are the same rule written for SQL; a test runs both over every status.
/// </summary>
public static class RentInstallmentRules
{
    /// <summary>
    /// Not paid and past its due date (Europe/Rome calendar day): <see cref="RentLedgerStatus.Scheduled"/> or
    /// <see cref="RentLedgerStatus.Failed"/>. A payment in flight (<see cref="RentLedgerStatus.Processing"/>) is not overdue
    /// until Stripe settles it; a <see cref="RentLedgerStatus.Cancelled"/> installment is no longer expected.
    /// </summary>
    public static bool IsOverdue(RentLedgerStatus status, DateOnly dueDate, DateOnly today) =>
        (status == RentLedgerStatus.Scheduled || status == RentLedgerStatus.Failed) && dueDate < today;

    /// <summary>Still to be collected: not paid and not cancelled (scheduled, in flight or failed).</summary>
    public static bool IsOpen(RentLedgerStatus status) =>
        status == RentLedgerStatus.Scheduled || status == RentLedgerStatus.Processing || status == RentLedgerStatus.Failed;

    /// <summary>
    /// The landlord can ask the tenants to pay: not paid, not in flight, not cancelled (<see cref="RentLedgerStatus.Scheduled"/>
    /// or <see cref="RentLedgerStatus.Failed"/>, whether or not it is past due).
    /// </summary>
    public static bool IsRemindable(RentLedgerStatus status) =>
        status == RentLedgerStatus.Scheduled || status == RentLedgerStatus.Failed;

    /// <summary><see cref="IsOverdue"/> for SQL.</summary>
    public static Expression<Func<RentLedgerEntry, bool>> OverdueOn(DateOnly today) =>
        e => (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate < today;

    /// <summary>
    /// Expected and neither collected nor overdue: a payment in flight, or not paid and not yet past due. Together with the
    /// paid ones and <see cref="OverdueOn"/> it covers every installment that is not cancelled, each in exactly one.
    /// </summary>
    public static Expression<Func<RentLedgerEntry, bool>> PendingOn(DateOnly today) =>
        e => e.Status == RentLedgerStatus.Processing
             || ((e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate >= today);

    /// <summary><see cref="IsOpen"/> for SQL.</summary>
    public static Expression<Func<RentLedgerEntry, bool>> Open { get; } =
        e => e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Processing || e.Status == RentLedgerStatus.Failed;

    /// <summary>Whole days from the due date to <paramref name="today"/> for an overdue installment, <c>null</c> otherwise.</summary>
    public static int? DaysOverdue(RentLedgerStatus status, DateOnly dueDate, DateOnly today) =>
        IsOverdue(status, dueDate, today) ? today.DayNumber - dueDate.DayNumber : null;
}
