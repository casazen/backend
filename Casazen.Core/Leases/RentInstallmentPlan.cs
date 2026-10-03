using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>One installment of a rent plan: the period it pays, its due date and amount.</summary>
public sealed record RentInstallment(DateOnly PeriodStart, DateOnly PeriodEnd, DateOnly DueDate, decimal Amount);

/// <summary>
/// The installments of a lease, and the final period shorter than the cadence when the lease ends inside one
/// (<see cref="PartialFinalPeriod"/>): no amount is computed for it (a pro rata rule is a contract choice), the landlord
/// handles it.
/// </summary>
public sealed record RentPlan(IReadOnlyList<RentInstallment> Installments, RentPeriod? PartialFinalPeriod);

/// <summary>A period of the lease, both days included.</summary>
public sealed record RentPeriod(DateOnly Start, DateOnly End);

/// <summary>
/// Rent schedule of a lease (LT-06, #269), computed from the lease only: no amount or date invented.
/// <list type="bullet">
/// <item>Periods are anchored on the lease start: period <c>i</c> runs from <c>start + i × months</c> to the day before the
/// next one (<see cref="MonthsPer"/>), so a lease starting on the 1st has calendar-month periods.</item>
/// <item>The installment is due on the first <see cref="Entities.RentSchedule.BillingDayOfMonth"/> on or after the start of its
/// period (in advance for a lease starting on the 1st with due day 5: due on the 5th of the month it pays).</item>
/// <item>Only whole periods inside the lease get an installment; a shorter final period is returned apart.</item>
/// </list>
/// </summary>
public static class RentInstallmentPlan
{
    public const int MinBillingDay = 1;

    /// <summary>At most 28, so the due day exists in every month (no clamping rule to choose).</summary>
    public const int MaxBillingDay = 28;

    /// <summary>Upper bound of the generated installments (a 4+4 monthly lease has 96): protects against absurd dates.</summary>
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

    /// <summary>Default due day: the day of the lease start, at most <see cref="MaxBillingDay"/>.</summary>
    public static int DefaultBillingDay(DateOnly leaseStart) => Math.Min(leaseStart.Day, MaxBillingDay);

    /// <summary>Default installment amount: the lease's monthly rent times the months of the cadence.</summary>
    public static decimal DefaultAmount(decimal monthlyRent, RentCadence cadence) =>
        decimal.Round(monthlyRent * MonthsPer(cadence), 2, MidpointRounding.AwayFromZero);

    /// <summary>The first <paramref name="billingDay"/> of a month on or after <paramref name="periodStart"/>.</summary>
    public static DateOnly DueDate(DateOnly periodStart, int billingDay)
    {
        if (!IsValidBillingDay(billingDay))
            throw new ArgumentOutOfRangeException(nameof(billingDay), billingDay, "The due day must be between 1 and 28");

        var sameMonth = new DateOnly(periodStart.Year, periodStart.Month, billingDay);
        return sameMonth >= periodStart ? sameMonth : sameMonth.AddMonths(1);
    }

    /// <summary>
    /// The installments from <paramref name="leaseStart"/> to <paramref name="leaseEnd"/> (both included), with
    /// <paramref name="amount"/> each.
    /// </summary>
    public static RentPlan Build(DateOnly leaseStart, DateOnly leaseEnd, RentCadence cadence, int billingDay, decimal amount)
    {
        if (leaseEnd < leaseStart)
            throw new ArgumentException("The lease ends before it starts", nameof(leaseEnd));
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The installment amount must be positive");

        var months = MonthsPer(cadence);
        var installments = new List<RentInstallment>();
        RentPeriod? partial = null;
        for (var i = 0; i < MaxInstallments; i++)
        {
            // Always from the lease start (AddMonths clamps to the month end: 31 Jan + 1 = 28/29 Feb, + 2 = 31 Mar).
            var start = leaseStart.AddMonths(i * months);
            if (start > leaseEnd)
                break;

            var end = leaseStart.AddMonths((i + 1) * months).AddDays(-1);
            if (end > leaseEnd)
            {
                partial = new RentPeriod(start, leaseEnd);
                break;
            }

            installments.Add(new RentInstallment(start, end, DueDate(start, billingDay), amount));
        }

        return new RentPlan(installments, partial);
    }
}
