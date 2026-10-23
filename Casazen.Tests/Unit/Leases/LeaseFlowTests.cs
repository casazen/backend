using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-01b: the one rule of the path of a lease where a mistake costs data. A lease that nobody gave notice of renews by itself (LR-14),
/// so the day its period ends it is not over, and showing it as concluded would hide a running lease (its rent, its deadlines, the
/// property it ties up) from the landlord; the other way round, a lease whose notice or early termination has taken effect, or that does not
/// renew, must not stay "active" after its last day. The table is the boundaries of that rule, one row each, reported together.
/// </summary>
public class LeaseFlowTests
{
    private static readonly DateOnly Today = new(2026, 10, 9);

    /// <summary>A registered 4+4 lease, renewed into 2030, with its rent plan: active and with nothing to do.</summary>
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
    public void Derive_TheEndOfThePeriod_NeverMakesARenewingLeaseConcluded_NorKeepsAnEndedOneActive()
    {
        var yesterday = Today.AddDays(-1);
        var cases = new (string Case, LeaseFlowInput Input, LeaseStage Stage, LeaseNextStepCode Step)[]
        {
            ("renewed into 2030", Running, LeaseStage.Active, LeaseNextStepCode.AllGood),
            ("period ended yesterday, no notice: it renews by itself, the job has not run yet", Running with { CurrentTermEndDate = yesterday }, LeaseStage.Expiring, LeaseNextStepCode.DecideNotice),
            ("period ended yesterday, notice given", Running with { CurrentTermEndDate = yesterday, NoticeOn = new DateOnly(2026, 3, 1) }, LeaseStage.Concluded, LeaseNextStepCode.Archive),
            ("period ends today, notice given: still in force on its last day", Running with { CurrentTermEndDate = Today, NoticeOn = new DateOnly(2026, 3, 1) }, LeaseStage.Expiring, LeaseNextStepCode.OrganizeEnd),
            ("ended early, last day yesterday", Running with { TerminatedOn = yesterday, CurrentTermEndDate = yesterday }, LeaseStage.Concluded, LeaseNextStepCode.Archive),
            ("ended early, last day today", Running with { TerminatedOn = Today, CurrentTermEndDate = Today }, LeaseStage.Expiring, LeaseNextStepCode.OrganizeEnd),
            ("ended early, last day in the future", Running with { TerminatedOn = new DateOnly(2027, 1, 31), CurrentTermEndDate = new DateOnly(2027, 1, 31) }, LeaseStage.Expiring, LeaseNextStepCode.OrganizeEnd),
            ("transitory, period ended yesterday: it does not renew", Running with { ContractType = LeaseContractType.Transitorio, CurrentTermEndDate = yesterday }, LeaseStage.Concluded, LeaseNextStepCode.Archive),
            ("transitory, ends today", Running with { ContractType = LeaseContractType.Transitorio, CurrentTermEndDate = Today }, LeaseStage.Expiring, LeaseNextStepCode.OrganizeEnd),
            ("ends in exactly six months", Running with { CurrentTermEndDate = Today.AddMonths(6) }, LeaseStage.Expiring, LeaseNextStepCode.DecideNotice),
            ("ends one day later than six months", Running with { CurrentTermEndDate = Today.AddMonths(6).AddDays(1) }, LeaseStage.Active, LeaseNextStepCode.AllGood),
            ("signed, brought in already over and recorded as concluded", Running with { Status = LeaseStatus.Signed, Origin = LeaseOrigin.Imported, TerminatedOn = new DateOnly(2024, 1, 2), CurrentTermEndDate = new DateOnly(2024, 1, 2) }, LeaseStage.Concluded, LeaseNextStepCode.Archive),
            ("signed, period over, renewal pending: still to be registered", Running with { Status = LeaseStatus.Signed, CurrentTermEndDate = yesterday }, LeaseStage.Signed, LeaseNextStepCode.RegisterLease),
            ("rejected", Running with { Status = LeaseStatus.Rejected }, LeaseStage.Concluded, LeaseNextStepCode.Archive),
            (
                "a draft whose dates are in the past is still a draft",
                new LeaseFlowInput { Today = Today, Status = LeaseStatus.Draft, ContractType = LeaseContractType.Transitorio, CurrentTermEndDate = new DateOnly(2024, 1, 1) },
                LeaseStage.Draft,
                LeaseNextStepCode.CompleteStep),
        };

        var failures = new List<string>();
        foreach (var (name, input, stage, step) in cases)
        {
            var result = LeaseFlow.Derive(input);
            if (result.Stage != stage || result.NextStep.Code != step)
                failures.Add($"{name}: got {result.Stage}/{result.NextStep.Code}, expected {stage}/{step}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
