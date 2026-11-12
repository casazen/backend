using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Utilities;

namespace Casazen.Core.Leases;

public static class TenantSepaAuthorization
{
    public const decimal TransactionFee = 1m;
    public static DateOnly BlockedFrom(LeaseContract lease)
    {
        var end = RomeCalendar.DateInRome(lease.CurrentTermEndDate);
        return new DateOnly(end.Year, end.Month, 1).AddMonths(1);
    }
    public static bool CanDebit(LeaseContract lease, DateOnly today) =>
        LeaseRenewal.IsInForce(lease.Status) && lease.PartiesAnonymizedAt is null
        && today >= RomeCalendar.DateInRome(lease.StartDate) && today < BlockedFrom(lease);
    public static bool CanAutomate(LeaseContract lease, RentSchedule schedule, RentLedgerEntry entry, DateOnly today) =>
        CanDebit(lease, today) && schedule.IsActive && schedule.SepaAutomaticEnabled
        && schedule.Cadence == RentCadence.Monthly && schedule.SepaPaymentMethodId is not null
        && schedule.SepaAutomaticFrom is { } from && entry.DueDate >= from && entry.DueDate <= today
        && entry.PeriodStart <= RomeCalendar.DateInRome(lease.CurrentTermEndDate)
        && entry.Status == RentLedgerStatus.Scheduled && entry.SepaAutomaticAttemptedAt is null;
}
