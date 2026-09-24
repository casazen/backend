using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// CO-07 (A5-21): rules of the D.L. 145/2023 art. 13-ter safety checklist as verified by RS-3 in
/// <c>.claude/context/regulations/sicurezza.md</c>.
/// </summary>
public class SafetyChecklistRulesTests
{
    [Theory]
    [InlineData(1, new[] { 80.0 }, 1)]
    [InlineData(1, new[] { 200.0 }, 1)]
    [InlineData(1, new[] { 201.0 }, 2)]
    [InlineData(1, new[] { 400.0 }, 2)]
    [InlineData(2, new[] { 250.0, 50.0 }, 3)]
    [InlineData(3, new[] { 10.0, 10.0, 10.0 }, 3)]
    public void MinimumExtinguishers_FloorAreas_OnePer200SqmOrFractionOnEachFloor(int floors, double[] areas, int expected)
    {
        var minimum = SafetyChecklistRules.MinimumExtinguishers(floors, areas.Select(a => (decimal)a).ToList());

        Assert.Equal(expected, minimum);
    }

    [Fact]
    public void MinimumExtinguishers_NoAreas_OnePerFloor()
    {
        Assert.Equal(2, SafetyChecklistRules.MinimumExtinguishers(2, null));
        Assert.Null(SafetyChecklistRules.MinimumExtinguishers(null, null));
    }

    [Fact]
    public void Evaluate_AllElectricUnitWithExtinguisher_GasAndCoNotApplicableAndComplete()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.True(evaluation.IsComplete);
        Assert.Empty(evaluation.Warnings);
        foreach (var code in new[] { SafetyItemCode.GasDetector, SafetyItemCode.CoDetector })
        {
            var item = evaluation.Items.Single(i => i.Code == code);
            Assert.Equal(SafetyItemStatus.NotApplicable, item.Status);
            Assert.Equal(SafetyNotApplicableReason.NoGasNoCombustion, item.NotApplicableReason);
        }
    }

    [Theory]
    [InlineData(false, CombustionAppliance.Fireplace)]
    [InlineData(false, CombustionAppliance.Stove)]
    [InlineData(true, null)]
    public void Evaluate_GasOrCombustionWithoutDetectors_BothDetectorsBlock(bool hasGas, CombustionAppliance? appliance)
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.HasGasSupply = hasGas;
        checklist.CombustionAppliances = appliance is { } a ? [a] : [];

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.Equal(
            ["safety_gas_detector_missing", "safety_co_detector_missing"],
            evaluation.Blockers.Select(b => b.Code));
        Assert.All(
            evaluation.Items.Where(i => i.Code is SafetyItemCode.GasDetector or SafetyItemCode.CoDetector),
            i => Assert.Equal(SafetyItemRequirement.Required, i.Requirement));
    }

    [Fact]
    public void Evaluate_GasWithDetectorsPresent_Complete()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.HasGasSupply = true;
        checklist.CombustionAppliances = [CombustionAppliance.GasHob, CombustionAppliance.Boiler];
        foreach (var item in checklist.Items.Where(i => i.Code is SafetyItemCode.GasDetector or SafetyItemCode.CoDetector))
            item.Answer = SafetyItemAnswer.Present;

        Assert.True(SafetyChecklistRules.Evaluate(checklist).IsComplete);
    }

    [Fact]
    public void Evaluate_GasFactsUnanswered_QuestionBlocksNotTheDetectors()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.HasGasSupply = null;
        checklist.CombustionAppliances = null;

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.Equal(["safety_gas_unanswered"], evaluation.Blockers.Select(b => b.Code));
        Assert.Equal(
            SafetyItemRequirement.Undetermined,
            evaluation.Items.Single(i => i.Code == SafetyItemCode.GasDetector).Requirement);
    }

    [Fact]
    public void Evaluate_NotEntrepreneurial_SystemsComplianceNotApplicable()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());

        var item = SafetyChecklistRules.Evaluate(checklist).Items.Single(i => i.Code == SafetyItemCode.SystemsCompliance);

        Assert.Equal(SafetyItemStatus.NotApplicable, item.Status);
        Assert.Equal(SafetyNotApplicableReason.NotEntrepreneurial, item.NotApplicableReason);
    }

    [Fact]
    public void Evaluate_EntrepreneurialWithoutCompliantSystems_Blocks()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.Entrepreneurial = true;

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.Equal(["safety_systems_compliance_missing"], evaluation.Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Evaluate_OptionalItemsMissing_NeverBlock()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        foreach (var item in checklist.Items.Where(i => i.Code is SafetyItemCode.SmokeDetector or SafetyItemCode.EmergencyInstructions))
            item.Answer = SafetyItemAnswer.Missing;

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.True(evaluation.IsComplete);
        Assert.Equal(
            SafetyItemRequirement.Optional,
            evaluation.Items.Single(i => i.Code == SafetyItemCode.SmokeDetector).Requirement);
    }

    [Fact]
    public void Evaluate_ExtinguishersBelowMinimum_BlocksWithDeclaredAndMinimum()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.FloorCount = 2;
        checklist.FloorAreasSqm = [250m, 50m];

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        var blocker = Assert.Single(evaluation.Blockers);
        Assert.Equal("safety_extinguishers_below_minimum", blocker.Code);
        Assert.Equal(new object[] { 1, 3 }, blocker.MessageArgs);
        Assert.Equal(3, evaluation.MinimumExtinguishers);
    }

    [Fact]
    public void Evaluate_FloorsWithoutAreas_WarnsAndCountsOnePerFloor()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.FloorAreasSqm = null;

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.True(evaluation.IsComplete);
        Assert.Equal(1, evaluation.MinimumExtinguishers);
        Assert.Equal(["safety_floor_areas_missing"], evaluation.Warnings.Select(w => w.Code));
    }

    [Fact]
    public void Evaluate_NothingSaved_BlocksOnQuestionsRequiredItemsAndConfirmation()
    {
        var evaluation = SafetyChecklistRules.Evaluate(null);

        Assert.Equal(
            [
                "safety_entrepreneurial_unanswered",
                "safety_gas_unanswered",
                "safety_floors_unanswered",
                "safety_extinguishers_missing",
                "safety_bdsr_declaration_missing",
                "safety_confirmation_missing",
            ],
            evaluation.Blockers.Select(b => b.Code));
        Assert.Equal(SafetyChecklistRules.Items, evaluation.Items.Select(i => i.Code));
    }

    [Fact]
    public void Evaluate_ConfirmationOfAnOlderText_Blocks()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.ConfirmedTextVersion = "2025-01-v0";

        Assert.Equal(["safety_confirmation_missing"], SafetyChecklistRules.Evaluate(checklist).Blockers.Select(b => b.Code));
    }

    [Fact]
    public void Evaluate_ImportedAnswersToReview_BlockWithReviewCodes()
    {
        var checklist = SafetyChecklistTestData.CompleteAllElectric(Guid.NewGuid(), Guid.NewGuid());
        checklist.Entrepreneurial = true;
        foreach (var item in checklist.Items.Where(i => i.Code is SafetyItemCode.FireExtinguishers or SafetyItemCode.SystemsCompliance))
            item.Answer = SafetyItemAnswer.ToReview;

        var evaluation = SafetyChecklistRules.Evaluate(checklist);

        Assert.Equal(
            ["safety_extinguishers_review", "safety_systems_compliance_review"],
            evaluation.Blockers.Select(b => b.Code));
        Assert.Equal(
            SafetyItemStatus.ToReview,
            evaluation.Items.Single(i => i.Code == SafetyItemCode.FireExtinguishers).Status);
    }
}
