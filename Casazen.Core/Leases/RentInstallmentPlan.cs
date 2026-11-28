using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

public sealed record RentInstallment(DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate, decimal Amount);

public sealed record RentPlan(IReadOnlyList<RentInstallment> Installments, RentPeriod? PartialFinalPeriod);

public sealed record RentPeriod(DateOnly Start, DateOnly End);

/// <summary>
/// Rent schedule of a lease (LT-06, #269), computed from the lease only: no amount or date invented.
/// <list type="bullet">
/// <item>Periods are anchored on the lease start: period <c>i</c> runs from <c>start + i × months</c> to the day before the
/// next one (<see cref="MonthsPer"/>), so a lease starting on the 1st has calendar-month periods.</item>
/// <item>The installment is due on the first <see cref="Entities.RentSchedule.BillingDayOfMonth"/> on or after the start of its
/// period (in advance for a lease starting on the 1st with due day 5: due on the 5th of the month it pays).</item>
/// <item>Only whole periods inside the lease get an installment; a shorter final period is returned apart.</item>
/// <item>A lease already running when its rents are set up is tracked from a day (LR-04a, <c>trackFrom</c>): the periods that
/// ended before it get no installment, instead of being born overdue.</item>
/// </list>
/// </summary>
public static class RentInstallmentPlan
{
    public const int MinBillingDay = 1;

    public const int MaxBillingDay = 28;

    public const int MaxInstallments = 600;

    public static int MonthsPer(RentCadence cadence) => cadence switch
    {
        RentCadence.Monthly => 1,
        RentCadence.Bimonthly => 2,
        RentCadence.Quarterly => 3,
        RentCadence.Semiannual => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown rent cadence"),
    };

    public static bool IsValidBillingDay(int day) => day is >= MinBillingDay and <= MaxBillingDay;

    public static int DefaultBillingDay(DateOnly leaseStart) => Math.Min(leaseStart.Day, MaxBillingDay);

    public static decimal DefaultAmount(decimal monthlyRent, RentCadence cadence) =>
        decimal.Round(monthlyRent * MonthsPer(cadence), 2, MidpointRounding.AwayFromZero);

    public static DateOnly DueDate(DateOnly periodStart, int billingDay)
    {
        if (!IsValidBillingDay(billingDay))
            throw new ArgumentOutOfRangeException(nameof(billingDay), billingDay, "The due day must be between 1 and 28");

        var sameMonth = new DateOnly(periodStart.Year, periodStart.Month, billingDay);
        return sameMonth >= periodStart ? sameMonth : sameMonth.AddMonths(1);
    }

    public static RentPlan Build(
        DateOnly leaseStart, DateOnly leaseEnd, RentCadence cadence, int billingDay, decimal amount, DateOnly? trackFrom = null)
    {
        if (leaseEnd < leaseStart)
            throw new ArgumentException("The lease ends before it starts", nameof(leaseEnd));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The installment amount must be positive");

        var months = MonthsPer(cadence);
        var tracked = trackFrom ?? DateOnly.MinValue;
        var installments = new List<RentInstallment>();
        RentPeriod? partial = null;
        for (var i = 0; i < MaxInstallments; i++)
        {
            var start = leaseStart.AddMonths(i * months);
            if (start > leaseEnd)
                break;

            var end = leaseStart.AddMonths((i + 1) * months).AddDays(-1);
            if (end > leaseEnd)
            {
                if (leaseEnd >= tracked)
                    partial = new RentPeriod(start, leaseEnd);
                break;
            }

            if (end < tracked)
                continue;

            installments.Add(new RentInstallment(start, end, DueDate(start, billingDay), amount));
        }

        return new RentPlan(installments, partial);
    }
}
