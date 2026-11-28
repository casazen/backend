using System.Linq.Expressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

public static class RentInstallmentRules
{
    /// <summary>
    /// Not paid and past its due date (Europe/Rome calendar day): <see cref="RentLedgerStatus.Scheduled"/> or
    /// <see cref="RentLedgerStatus.Failed"/>. A payment in flight (<see cref="RentLedgerStatus.Processing"/>) is not overdue
    /// until Stripe settles it; a <see cref="RentLedgerStatus.Cancelled"/> installment is no longer expected.
    /// </summary>
    public static bool IsOverdue(RentLedgerStatus status, DateOnly dueDate, DateOnly today) =>
        (status == RentLedgerStatus.Scheduled || status == RentLedgerStatus.Failed) && dueDate < today;

    public static bool IsOpen(RentLedgerStatus status) =>
        status == RentLedgerStatus.Scheduled || status == RentLedgerStatus.Processing || status == RentLedgerStatus.Failed;

    public static bool IsRemindable(RentLedgerStatus status) =>
        status == RentLedgerStatus.Scheduled || status == RentLedgerStatus.Failed;

    public static Expression<Func<RentLedgerEntry, bool>> OverdueOn(DateOnly today) =>
        e => (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate < today;

    public static Expression<Func<RentLedgerEntry, bool>> PendingOn(DateOnly today) =>
        e => e.Status == RentLedgerStatus.Processing
             || ((e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed) && e.DueDate >= today);

    public static Expression<Func<RentLedgerEntry, bool>> Open { get; } =
        e => e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Processing || e.Status == RentLedgerStatus.Failed;

    /// <summary>Whole days from the due date to <paramref name="today"/> for an overdue installment, <c>null</c> otherwise.</summary>
    public static int? DaysOverdue(RentLedgerStatus status, DateOnly dueDate, DateOnly today) =>
        IsOverdue(status, dueDate, today) ? today.DayNumber - dueDate.DayNumber : null;

    public static decimal AmountPaid(RentLedgerStatus status, decimal amountDue) =>
        status == RentLedgerStatus.Paid ? amountDue : 0m;

    public static decimal AmountOutstanding(RentLedgerStatus status, decimal amountDue) =>
        amountDue - AmountPaid(status, amountDue);
}
