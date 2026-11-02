using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-02b: the numbers of the bar of the lease list. They are on every row of <c>GET api/leases</c>, so the function that works them out
/// must never throw, whatever the dates of the lease, and must say the same thing the words will: the step of the path before the
/// contract runs, the year or the month of the period after. One row for each shape.
/// </summary>
public class LeaseProgressTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    /// <summary>A registered 4+4 lease that started on 2022-11-01 and was renewed into 2030: in its fourth year of eight.</summary>
    private static readonly LeaseFlowInput Running = new()
    {
        Today = Today,
        Status = LeaseStatus.Registered,
        ContractType = LeaseContractType.Libero,
        StartDate = new DateOnly(2022, 11, 1),
        CurrentTermEndDate = new DateOnly(2030, 10, 31),
        HasActiveRentPlan = true,
    };

    [Fact]
    public void Derive_Progress_CountsTheStepsBeforeTheLeaseRunsAndThePeriodAfter_AndNeverThrows()
    {
        var cases = new (string Case, LeaseFlowInput Input, LeaseProgress Expected)[]
        {
            // 1439 of 2922 days gone: 49 %.
            ("running, fourth year of eight", Running, new LeaseProgress(LeaseProgressKind.Term, LeaseProgressUnit.Years, 4, 8, 49)),
            (
                "transitory of ten months, sixth month (162 of 304 days)",
                Running with { ContractType = LeaseContractType.Transitorio, StartDate = new DateOnly(2026, 5, 1), CurrentTermEndDate = new DateOnly(2027, 2, 28) },
                new LeaseProgress(LeaseProgressKind.Term, LeaseProgressUnit.Months, 6, 10, 53)),
            (
                "not begun yet: the first year, nothing gone",
                Running with { StartDate = Today.AddDays(10), CurrentTermEndDate = Today.AddYears(4) },
                new LeaseProgress(LeaseProgressKind.Term, LeaseProgressUnit.Years, 1, 4, 0)),
            ("signed, the registration still to do", Running with { Status = LeaseStatus.Signed }, new LeaseProgress(LeaseProgressKind.Setup, LeaseProgressUnit.Steps, 7, 8, 87)),
            ("handed out for signature", Running with { Status = LeaseStatus.Draft, SentForSignatureOn = Today }, new LeaseProgress(LeaseProgressKind.Setup, LeaseProgressUnit.Steps, 6, 8, 75)),
            ("concluded", Running with { Status = LeaseStatus.Rejected }, new LeaseProgress(LeaseProgressKind.End, LeaseProgressUnit.Steps, 8, 8, 100)),
            (
                "a draft with nothing known of its steps",
                new LeaseFlowInput { Today = Today, Status = LeaseStatus.Draft },
                new LeaseProgress(LeaseProgressKind.Setup, LeaseProgressUnit.Steps, 0, 8, 0)),
        };

        var failures = new List<string>();
        foreach (var (name, input, expected) in cases)
        {
            var got = LeaseFlow.Derive(input).Progress;
            if (got != expected)
                failures.Add($"{name}: got {got}, expected {expected}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
