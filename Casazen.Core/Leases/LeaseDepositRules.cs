using Casazen.Core.Entities;

namespace Casazen.Core.Leases;

public sealed record LeaseDepositRate(int Year, decimal RatePercent);

public sealed record LeaseDepositDeduction(string Reason, decimal Amount);

public sealed record LeaseDepositReturnInput(
    DateOnly ReturnedOn,
    DateOnly? DepositPaidOn,
    IReadOnlyList<LeaseDepositRate> Rates,
    decimal InterestPaid,
    IReadOnlyList<LeaseDepositDeduction> Deductions);

public sealed record LeaseDepositYearInterest(int Year, decimal RatePercent, int Days, int DaysInYear, decimal Interest);

public sealed record LeaseDepositCalculation(
    decimal Deposit,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<LeaseDepositYearInterest> Years,
    decimal Interest,
    decimal InterestPaid,
    decimal InterestDue,
    IReadOnlyList<LeaseDepositDeduction> Deductions,
    decimal DeductionsTotal,
    decimal Returned);

public enum LeaseDepositProblem
{
    None = 0,

    DatesInvalid = 1,

    RateInvalid = 2,

    RateMissing = 3,

    InterestPaidInvalid = 4,

    DeductionInvalid = 5,

    DeductionsExceed = 6,
}

public sealed record LeaseDepositOutcome(LeaseDepositCalculation? Calculation, LeaseDepositProblem Problem, IReadOnlyList<int> MissingYears);

/// <summary>
/// The rules of the return of the security deposit (LR-OPS-1c), pure. The deposit produces legal interest (art. 11 L. 392/1978) that the
/// landlord pays the tenant every year of the lease and, for the last stretch, when he returns the deposit; the landlord may keep from it what
/// he documents (rent or expenses not paid, damage beyond the normal wear). The legal rate changes every year and is not in the project: the
/// landlord types the rate of each calendar year the deposit was held, and the interest of a year is <c>deposit × rate / 100 × days / days of
/// the year</c> (365 or 366), rounded to the cent <b>year by year</b> (the statement shows each year and the total is their sum). The days
/// are counted from the day the deposit was paid to the day before it is returned: the day of the return earns nothing.
/// </summary>
public static class LeaseDepositRules
{
    public const int MaxDeductions = 20;

    public const int MaxReasonLength = 200;

    public const decimal MaxRatePercent = 100m;

    public const decimal MaxAmount = 10_000_000m;

    public const int MaxYearsAfterEnd = 3;

    public static bool Applies(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return LeaseRenewal.IsInForce(lease.Status) && LeaseOperationRules.IsEnding(lease) && lease.SecurityDeposit is > 0;
    }

    public static IReadOnlyList<int> YearsHeld(DateOnly from, DateOnly to)
    {
        if (to <= from)
            return [];

        var years = new List<int>();
        for (var year = from.Year; year <= to.AddDays(-1).Year; year++)
            years.Add(year);
        return years;
    }

    private static bool IsAmount(decimal value, bool above) => (above ? value > 0 : value >= 0) && value <= MaxAmount && decimal.Round(value, 2) == value;

    private static bool IsRate(decimal value) => value >= 0 && value <= MaxRatePercent && decimal.Round(value, 2) == value;

    public static LeaseDepositOutcome Calculate(
        decimal deposit,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<LeaseDepositRate> rates,
        decimal interestPaid,
        IReadOnlyList<LeaseDepositDeduction> deductions)
    {
        ArgumentNullException.ThrowIfNull(rates);
        ArgumentNullException.ThrowIfNull(deductions);
        if (deposit <= 0)
            throw new ArgumentOutOfRangeException(nameof(deposit), deposit, "The deposit must be above zero");

        static LeaseDepositOutcome Refused(LeaseDepositProblem problem, IReadOnlyList<int>? missing = null) => new(null, problem, missing ?? []);

        if (to <= from)
            return Refused(LeaseDepositProblem.DatesInvalid);

        var byYear = new Dictionary<int, decimal>();
        foreach (var rate in rates)
        {
            if (!IsRate(rate.RatePercent) || !byYear.TryAdd(rate.Year, rate.RatePercent))
                return Refused(LeaseDepositProblem.RateInvalid);
        }

        var years = new List<LeaseDepositYearInterest>();
        var missing = new List<int>();
        foreach (var year in YearsHeld(from, to))
        {
            var start = from > new DateOnly(year, 1, 1) ? from : new DateOnly(year, 1, 1);
            var end = to < new DateOnly(year + 1, 1, 1) ? to : new DateOnly(year + 1, 1, 1);
            var days = end.DayNumber - start.DayNumber;
            if (!byYear.TryGetValue(year, out var ratePercent))
            {
                missing.Add(year);
                continue;
            }

            var daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
            var interest = decimal.Round(deposit * ratePercent / 100m * days / daysInYear, 2, MidpointRounding.AwayFromZero);
            years.Add(new LeaseDepositYearInterest(year, ratePercent, days, daysInYear, interest));
        }

        if (missing.Count > 0)
            return Refused(LeaseDepositProblem.RateMissing, missing);

        var accrued = years.Sum(y => y.Interest);
        if (!IsAmount(interestPaid, above: false) || interestPaid > accrued)
            return Refused(LeaseDepositProblem.InterestPaidInvalid);

        if (deductions.Count > MaxDeductions)
            return Refused(LeaseDepositProblem.DeductionInvalid);

        var kept = new List<LeaseDepositDeduction>();
        foreach (var deduction in deductions)
        {
            var reason = deduction.Reason?.Trim() ?? string.Empty;
            if (reason.Length == 0 || reason.Length > MaxReasonLength || !IsAmount(deduction.Amount, above: true))
                return Refused(LeaseDepositProblem.DeductionInvalid);
            kept.Add(new LeaseDepositDeduction(reason, deduction.Amount));
        }

        var due = accrued - interestPaid;
        var total = kept.Sum(d => d.Amount);
        var returned = deposit + due - total;
        if (returned < 0)
            return Refused(LeaseDepositProblem.DeductionsExceed);

        return new LeaseDepositOutcome(
            new LeaseDepositCalculation(deposit, from, to, years, accrued, interestPaid, due, kept, total, returned),
            LeaseDepositProblem.None,
            []);
    }
}
