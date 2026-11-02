using Casazen.Core.Entities;

namespace Casazen.Core.Leases;

/// <summary>The legal interest rate, in percent per year, of one calendar year (<c>RatePercent</c>): typed by the landlord, the rates are not in the project.</summary>
public sealed record LeaseDepositRate(int Year, decimal RatePercent);

/// <summary>A sum the landlord keeps from the deposit and why (<c>Reason</c>, a short sentence the tenant reads in the statement).</summary>
public sealed record LeaseDepositDeduction(string Reason, decimal Amount);

/// <summary>
/// What the landlord gives to work out the statement of the return of a deposit: the day it is returned, the day it was paid (the start of the lease
/// when not given), the legal rate of each calendar year it was held, the interest already paid to the tenant and the sums he keeps.
/// </summary>
public sealed record LeaseDepositReturnInput(
    DateOnly ReturnedOn,
    DateOnly? DepositPaidOn,
    IReadOnlyList<LeaseDepositRate> Rates,
    decimal InterestPaid,
    IReadOnlyList<LeaseDepositDeduction> Deductions);

/// <summary>The interest of one calendar year: the rate, the days of the year the deposit was held, the days of that year and the interest to the cent.</summary>
public sealed record LeaseDepositYearInterest(int Year, decimal RatePercent, int Days, int DaysInYear, decimal Interest);

/// <summary>
/// The statement of the return of a deposit (LR-OPS-1c): the deposit, the legal interest of each year it was held, the interest the landlord
/// already paid, the deductions and what is left to return (<c>Returned</c>), all in euros to the cent.
/// </summary>
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

/// <summary>Why a statement of the return of a deposit cannot be worked out (<see cref="LeaseDepositRules.Calculate"/>).</summary>
public enum LeaseDepositProblem
{
    None = 0,

    /// <summary>The deposit was not held for at least a day: the day it is returned is not after the day it was paid.</summary>
    DatesInvalid = 1,

    /// <summary>A rate is below zero, above the bound, has more than two decimals, or a year has two rates.</summary>
    RateInvalid = 2,

    /// <summary>A year the deposit was held in has no rate (<c>MissingYears</c> says which).</summary>
    RateMissing = 3,

    /// <summary>The interest already paid is below zero, has more than two decimals or is more than the interest accrued.</summary>
    InterestPaidInvalid = 4,

    /// <summary>A deduction has no reason, a reason that is too long, an amount that is not above zero or has more than two decimals, or there are too many.</summary>
    DeductionInvalid = 5,

    /// <summary>The deductions are more than the deposit and the interest due: what is left is not a return but a debt of the tenant.</summary>
    DeductionsExceed = 6,
}

/// <summary>The result of <see cref="LeaseDepositRules.Calculate"/>: the statement, or why there is none.</summary>
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
    /// <summary>At most this many deductions in one statement: a sanity bound, not a figure of law.</summary>
    public const int MaxDeductions = 20;

    /// <summary>The longest reason of a deduction, in characters: it is printed in the statement the tenant reads.</summary>
    public const int MaxReasonLength = 200;

    /// <summary>The highest rate that can be typed, in percent: a guard against a typo, not a figure of law (the legal rate has never been near it).</summary>
    public const decimal MaxRatePercent = 100m;

    /// <summary>The largest sum of a deduction or of the interest paid, in euros: a guard against a typo.</summary>
    public const decimal MaxAmount = 10_000_000m;

    /// <summary>How many years after the end of the lease a return can still be dated: a sanity bound.</summary>
    public const int MaxYearsAfterEnd = 3;

    /// <summary>
    /// The lease is going to end or has ended, and it has a deposit: the handover and the statement of the return belong to this moment
    /// (<see cref="LeaseOperationRules.IsEnding"/>).
    /// </summary>
    public static bool Applies(LeaseContract lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return LeaseRenewal.IsInForce(lease.Status) && LeaseOperationRules.IsEnding(lease) && lease.SecurityDeposit is > 0;
    }

    /// <summary>The calendar years that have at least one day in <c>[from, to)</c>, in order.</summary>
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

    /// <summary>
    /// The statement of the return of a <paramref name="deposit"/> paid on <paramref name="from"/> and returned on <paramref name="to"/>, with the
    /// legal <paramref name="rates"/> of the years it was held, the interest the landlord already paid and the <paramref name="deductions"/> he keeps.
    /// </summary>
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
