using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Xunit;
using static Casazen.Tests.Unit.Leases.LeaseDraftTestData;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-12: the review of a draft — blocks, warnings, things to know and what is fine — with the code, the step and the parameters of each
/// check, and the rule that the blocks are the needs of the flow.
/// </summary>
public class LeaseReviewTests
{
    private static IReadOnlyList<LeaseReviewCheck> Review(LeaseDraftProgressInput input, LeaseReviewFacts? facts = null) =>
        LeaseReview.Compute(input, facts ?? new LeaseReviewFacts());

    private static IReadOnlyList<string> Codes(IReadOnlyList<LeaseReviewCheck> checks, LeaseReviewSeverity severity) =>
        checks.Where(c => c.Severity == severity).Select(c => c.Code).ToList();

    private static LeaseReviewCheck Check(IReadOnlyList<LeaseReviewCheck> checks, string code) => Assert.Single(checks, c => c.Code == code);

    // ---- a draft with nothing wrong ------------------------------------------------------------------------------

    [Fact]
    public void ACompleteDraft_HasNoBlock_AndEveryStepSaysItIsFine()
    {
        var checks = Review(Input(wizardMaxStep: 5));

        Assert.False(LeaseReview.HasBlock(checks));
        Assert.Empty(Codes(checks, LeaseReviewSeverity.Block));
        Assert.Equal(
            [
                LeaseNeedCodes.PropertyOk, LeaseNeedCodes.ApeOk, LeaseNeedCodes.CadastralOk, LeaseNeedCodes.LandlordsOk,
                LeaseNeedCodes.TenantsOk, LeaseNeedCodes.RentOk, LeaseNeedCodes.DepositOk, LeaseNeedCodes.StartOk,
            ],
            Codes(checks, LeaseReviewSeverity.Ok));
    }

    [Fact]
    public void TheOkChecks_CarryNumbersAndDates_NeverANameOrAFiscalCode()
    {
        var checks = Review(Input());

        Assert.Equal(1, Check(checks, LeaseNeedCodes.LandlordsOk).Params!["count"]);
        Assert.Equal(900m, Check(checks, LeaseNeedCodes.RentOk).Params!["amount"]);
        var deposit = Check(checks, LeaseNeedCodes.DepositOk).Params!;
        Assert.Equal(1800m, deposit["amount"]);
        Assert.Equal(2.0m, deposit["rents"]);
        var start = Check(checks, LeaseNeedCodes.StartOk);
        Assert.Equal(4, start.Step);
        Assert.Equal("2026-11-01", start.Params!["start"]);
        Assert.Equal("2030-10-31", start.Params["end"]);
        Assert.Equal(48, start.Params["months"]);

        var everything = string.Join('|', checks.SelectMany(c => c.Params?.Values ?? []).Select(v => v?.ToString()));
        Assert.DoesNotContain("Mario", everything);
        Assert.DoesNotContain("Rossi", everything);
        Assert.DoesNotContain(LandlordFiscalCode, everything);
        Assert.DoesNotContain("example.com", everything);
    }

    [Fact]
    public void TheChecks_AreListedWorstFirst_AndByStepInsideEachGroup()
    {
        var checks = Review(
            Input(Complete() with { StartDate = "2026-09-01", MonthlyRent = null }, facts: new LeaseDraftFacts { PropertyExists = true, PropertyIsShortMode = true }));

        var severities = checks.Select(c => (int)c.Severity).ToList();
        Assert.Equal(severities.Order(), severities);
        foreach (var group in checks.GroupBy(c => c.Severity))
            Assert.Equal(group.Select(c => c.Step).Order(), group.Select(c => c.Step));
    }

    // ---- blocks are the needs of the flow ------------------------------------------------------------------------

    [Fact]
    public void TheBlocks_AreTheNeedsOfTheDataSteps_SameCodeSameStepSameParameters()
    {
        var values = Complete() with
        {
            Landlords = [],
            Tenants = [Tenant() with { Residence = null }],
            MonthlyRent = null,
            StartDate = "soon",
        };
        var input = Input(values, facts: new LeaseDraftFacts { PropertyExists = true });

        var flow = LeaseDraftProgress.Compute(input);
        var blocks = Review(input).Where(c => c.Severity == LeaseReviewSeverity.Block).ToList();

        var needs = Enumerable.Range(1, 5).SelectMany(step => flow[step].Needs.Select(n => (n.Code, Step: step))).ToList();
        Assert.Equal(needs.OrderBy(n => n.Step).ThenBy(n => n.Code), blocks.Select(c => (c.Code, c.Step)).OrderBy(n => n.Step).ThenBy(n => n.Code));
        Assert.True(LeaseReview.HasBlock(Review(input)));
        Assert.Contains(blocks, c => c.Code == LeaseNeedCodes.TenantIncomplete && (int)c.Params!["index"]! == 0);
    }

    [Fact]
    public void ACompanyAsTenantUnderTheCedolare_IsABlock_OfStep3()
    {
        var checks = Review(Input(Complete() with { Tenants = [Tenant(CompanyFiscalCode)] }));

        var block = Check(checks, LeaseNeedCodes.CedolareNotAllowedCompany);
        Assert.Equal(LeaseReviewSeverity.Block, block.Severity);
        Assert.Equal(3, block.Step);
    }

    [Fact]
    public void ADepositOverThreeRents_IsABlock_AndTheOkLineIsNotThere()
    {
        var checks = Review(Input(Complete() with { SecurityDeposit = 3000m }));

        Assert.Equal(LeaseReviewSeverity.Block, Check(checks, LeaseNeedCodes.DepositTooHigh).Severity);
        Assert.DoesNotContain(checks, c => c.Code == LeaseNeedCodes.DepositOk);
        Assert.Contains(checks, c => c.Code == LeaseNeedCodes.RentOk);
    }

    // ---- warnings ------------------------------------------------------------------------------------------------

    [Fact]
    public void AStartDateAlreadyPast_IsAWarning()
    {
        var checks = Review(Input(Complete() with { StartDate = "2026-10-08" }));

        var warn = Check(checks, LeaseNeedCodes.StartInPast);
        Assert.Equal(LeaseReviewSeverity.Warn, warn.Severity);
        Assert.Equal(4, warn.Step);
        Assert.False(LeaseReview.HasBlock(checks));
    }

    [Theory]
    [InlineData("2026-10-09", false)] // today is not past
    [InlineData("2026-10-10", false)]
    [InlineData("2026-10-08", true)]
    public void TheStartDate_IsPastOnlyBeforeToday(string start, bool past)
    {
        var checks = Review(Input(Complete() with { StartDate = start }));

        Assert.Equal(past, checks.Any(c => c.Code == LeaseNeedCodes.StartInPast));
    }

    [Fact]
    public void AnotherContractOfTheSameProperty_InTheSameDates_IsAWarning_NotABlock()
    {
        var checks = Review(Input(), new LeaseReviewFacts { OverlappingLeases = 2 });

        var warn = Check(checks, LeaseNeedCodes.LeaseOverlap);
        Assert.Equal(LeaseReviewSeverity.Warn, warn.Severity);
        Assert.Equal(2, warn.Params!["count"]);
        Assert.False(LeaseReview.HasBlock(checks));
        Assert.DoesNotContain(Review(Input()), c => c.Code == LeaseNeedCodes.LeaseOverlap);
    }

    [Fact]
    public void APropertyLetForShortStays_IsAWarning_NotABlock()
    {
        var facts = new LeaseDraftFacts
        {
            PropertyExists = true,
            HasApe = true,
            HasApeIdentification = true,
            HasCadastralData = true,
            PropertyIsShortMode = true,
        };
        var checks = Review(Input(facts: facts));

        var warn = Check(checks, LeaseNeedCodes.PropertyShortMode);
        Assert.Equal(LeaseReviewSeverity.Warn, warn.Severity);
        Assert.Equal(1, warn.Step);
        Assert.False(LeaseReview.HasBlock(checks));
    }

    [Fact]
    public void ARentOutsideAnIndicativeBand_IsAWarning_InsideTheBand_IsFine()
    {
        var values = Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = 70 } };
        var outside = new LeaseDraftConcordatoOutcome { RentOutOfBand = true, Indicative = true, MinMonthly = 400m, MaxMonthly = 700m };
        var inside = new LeaseDraftConcordatoOutcome { MinMonthly = 400m, MaxMonthly = 1000m };

        var warned = Review(Input(values, concordato: outside));
        var fine = Review(Input(values, concordato: inside));

        Assert.Equal(LeaseReviewSeverity.Warn, Check(warned, LeaseNeedCodes.RentOutsideIndicativeBand).Severity);
        Assert.False(LeaseReview.HasBlock(warned));
        Assert.Equal(LeaseReviewSeverity.Ok, Check(fine, LeaseNeedCodes.RentInBand).Severity);
    }

    [Fact]
    public void ARentOutsideAVerifiedBand_IsABlock()
    {
        var values = Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = 70 } };
        var outside = new LeaseDraftConcordatoOutcome { RentOutOfBand = true, MinMonthly = 400m, MaxMonthly = 700m };

        var checks = Review(Input(values, concordato: outside));

        Assert.Equal(LeaseReviewSeverity.Block, Check(checks, LeaseNeedCodes.RentOutOfBand).Severity);
        Assert.DoesNotContain(checks, c => c.Code == LeaseNeedCodes.RentInBand);
    }

    // ---- things to know ------------------------------------------------------------------------------------------

    [Fact]
    public void ANonEuTenant_IsToKnow_AboutTheQuestura_AtStep8()
    {
        var checks = Review(Input(Complete() with { Tenants = [Tenant(citizenship: "US")] }));

        var info = Check(checks, LeaseNeedCodes.TenantExtraEu);
        Assert.Equal(LeaseReviewSeverity.Info, info.Severity);
        Assert.Equal(8, info.Step);
        Assert.DoesNotContain(Review(Input()), c => c.Code == LeaseNeedCodes.TenantExtraEu);
    }

    [Fact]
    public void ATemplateNotApproved_IsToKnow_NotABlock()
    {
        var checks = Review(Input(), new LeaseReviewFacts { TemplateNotApproved = true });

        var info = Check(checks, LeaseNeedCodes.TemplateNotApproved);
        Assert.Equal(LeaseReviewSeverity.Info, info.Severity);
        Assert.Equal(6, info.Step);
        Assert.False(LeaseReview.HasBlock(checks));
    }

    [Fact]
    public void ATemplateThatLacksData_IsAWarning()
    {
        Assert.Equal(
            LeaseReviewSeverity.Warn,
            Check(Review(Input(), new LeaseReviewFacts { TemplateDataMissing = true }), LeaseNeedCodes.TemplateDataMissing).Severity);
    }

    [Fact]
    public void ThePartiesWhoseDocumentsWereNotChecked_AreToKnow_WithTheirNumber()
    {
        var values = Complete() with
        {
            Landlords = [Landlord() with { DocumentsVerifiedOn = "2026-10-01" }],
            Tenants = [Tenant()],
        };

        var checks = Review(Input(values));

        Assert.Equal(1, Check(checks, LeaseNeedCodes.DocumentsNotVerified).Params!["count"]);
        Assert.Equal(LeaseReviewSeverity.Info, Check(checks, LeaseNeedCodes.DocumentsNotVerified).Severity);
        var allChecked = Complete() with
        {
            Landlords = [Landlord() with { DocumentsVerifiedOn = "2026-10-01" }],
            Tenants = [Tenant() with { DocumentsVerifiedOn = "2026-10-01" }],
        };
        Assert.DoesNotContain(Review(Input(allChecked)), c => c.Code == LeaseNeedCodes.DocumentsNotVerified);
    }

    // ---- an imported contract ------------------------------------------------------------------------------------

    [Fact]
    public void AnImportedContract_MissingTheApeAndTheCadastralData_IsWarnedNotBlocked()
    {
        var checks = Review(Input(origin: LeaseOrigin.Imported, facts: new LeaseDraftFacts { PropertyExists = true }));

        Assert.Equal(LeaseReviewSeverity.Warn, Check(checks, LeaseNeedCodes.ApeMissing).Severity);
        Assert.Equal(LeaseReviewSeverity.Warn, Check(checks, LeaseNeedCodes.CadastralMissing).Severity);
        Assert.False(LeaseReview.HasBlock(checks));
    }

    [Fact]
    public void AnImportedContract_HasNoTemplateNoticeAndNoDocumentsNotice()
    {
        var checks = Review(Input(origin: LeaseOrigin.Imported), new LeaseReviewFacts { TemplateNotApproved = true, TemplateDataMissing = true });

        Assert.DoesNotContain(checks, c => c.Code is LeaseNeedCodes.TemplateNotApproved or LeaseNeedCodes.TemplateDataMissing or LeaseNeedCodes.DocumentsNotVerified);
    }

    [Fact]
    public void AnImportedContract_WithADataProblem_StillBlocks()
    {
        var checks = Review(Input(Complete() with { MonthlyRent = null }, origin: LeaseOrigin.Imported));

        Assert.True(LeaseReview.HasBlock(checks));
    }

    // ---- nothing typed yet ---------------------------------------------------------------------------------------

    [Fact]
    public void ADraftWithOnlyTheProperty_BlocksOnEverythingMissing_AndSaysOnlyThePropertyIsFine()
    {
        var checks = Review(Input(new LeaseDraftValues { PropertyId = PropertyId }, wizardMaxStep: 1));

        Assert.Equal(
            [
                LeaseNeedCodes.LandlordMissing, LeaseNeedCodes.TenantMissing, LeaseNeedCodes.ContractTypeMissing,
                LeaseNeedCodes.TaxRegimeMissing, LeaseNeedCodes.RentMissing, LeaseNeedCodes.DepositMissing, LeaseNeedCodes.StartMissing,
            ],
            Codes(checks, LeaseReviewSeverity.Block));
        Assert.Equal([LeaseNeedCodes.PropertyOk, LeaseNeedCodes.ApeOk, LeaseNeedCodes.CadastralOk], Codes(checks, LeaseReviewSeverity.Ok));
    }
}
