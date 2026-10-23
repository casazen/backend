using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// What the rent plan does when the lease it belongs to changes its end (LR-14): a tacit renewal adds the installments of the new period, an
/// early termination cancels the ones that start after the last day. Both run under the same lock of the lease as every other change of its
/// installments and join the transaction of the caller (the renewal job, the termination), so the lease and its plan change together or not
/// at all. Neither writes to a tenant: the payment requests of the new installments are the daily collection job's, as for every
/// installment of the plan.
/// </summary>
public sealed partial class RentBillingService
{
    public async Task<int> ExtendScheduleToCurrentTermAsync(Guid leaseId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);

        // Read under the lock, after the renewal saved the new end of the lease in the same transaction.
        var lease = await db.LeaseContracts
            .AsNoTracking()
            .Where(l => l.Id == leaseId)
            .Select(l => new { l.OrgId, l.StartDate, l.CurrentTermEndDate })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Lease not found") { Code = "lease_not_found", MessageKey = "LeaseNotFound" };
        var schedule = await db.RentSchedules.SingleOrDefaultAsync(s => s.LeaseContractId == leaseId, cancellationToken);
        if (schedule is not { IsActive: true } || !RentInstallmentPlan.IsValidBillingDay(schedule.BillingDayOfMonth) || schedule.Amount <= 0)
            return 0;

        var start = RomeCalendar.DateInRome(lease.StartDate);
        var end = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        if (end < start)
            return 0;

        var existing = (await db.RentLedgerEntries
                .AsNoTracking()
                .Where(e => e.LeaseContractId == leaseId)
                .Select(e => e.PeriodStart)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // Only the whole periods after the ones the plan already has: an installment that exists (paid, in flight, rewritten by the landlord)
        // is never touched. The plan is built from the lease start with the cadence of the schedule, so the new periods continue the old ones,
        // and from the day the plan is tracked from (LR-04a): a plan that skipped the past of a lease already running never gets it back.
        var plan = RentInstallmentPlan.Build(start, end, schedule.Cadence, schedule.BillingDayOfMonth, schedule.Amount, schedule.TrackFrom);
        var now = UtcNow;
        var added = 0;
        foreach (var installment in plan.Installments.Where(i => !existing.Contains(i.PeriodStart)))
        {
            db.RentLedgerEntries.Add(new RentLedgerEntry
            {
                OrgId = lease.OrgId,
                LeaseContractId = leaseId,
                RentScheduleId = schedule.Id,
                PeriodStart = installment.PeriodStart,
                PeriodEnd = installment.PeriodEnd,
                DueDate = installment.DueDate,
                AmountDue = installment.Amount,
                Status = RentLedgerStatus.Scheduled,
                CreatedAt = now,
                UpdatedAt = now,
            });
            added++;
        }

        if (added > 0)
        {
            schedule.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Rent schedule of lease {LeaseId} extended to the new term: {Installments} installments added", leaseId, added);
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);
        return added;
    }

    public async Task<RentInstallmentsCancelled> CancelInstallmentsAfterAsync(
        Guid leaseId, DateOnly lastDay, CancellationToken cancellationToken = default)
    {
        await using var transaction = await LockLeaseAsync(leaseId, cancellationToken);

        // Still to be collected and starting after the last day. Paid and in-flight installments are not in the list, so they are never touched;
        // the one whose period contains the last day starts before it and stays as it is.
        var open = await db.RentLedgerEntries
            .Where(e => e.LeaseContractId == leaseId
                        && e.PeriodStart > lastDay
                        && (e.Status == RentLedgerStatus.Scheduled || e.Status == RentLedgerStatus.Failed))
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
            return new RentInstallmentsCancelled(0, []);

        var now = UtcNow;
        var cancelled = 0;
        var notices = new List<RentPaymentNotice>();
        foreach (var entry in open)
        {
            // A PaymentIntent the tenant could still pay is canceled first; one paid or in flight meanwhile wins and the installment stays.
            if (entry.StripePaymentIntentId is not null && entry.ConnectedAccountId is not null)
            {
                var current = await CancelIfPayableAsync(entry, cancellationToken);
                if (current.Status != "canceled")
                {
                    if (ApplyStripeStatus(entry, current) is { } notice)
                        notices.Add(notice);
                    continue;
                }
            }

            entry.Status = RentLedgerStatus.Cancelled;
            entry.StripePaymentIntentId = null;
            entry.PaymentTokenHash = null;
            entry.UpdatedAt = now;
            cancelled++;
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Rent installments of lease {LeaseId} after {LastDay} cancelled because the lease ended: {Cancelled} cancelled, {Kept} kept (paid or in flight)",
            leaseId, lastDay, cancelled, open.Count - cancelled);
        return new RentInstallmentsCancelled(cancelled, notices);
    }
}
