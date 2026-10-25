using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Xunit;
using static Casazen.Tests.Unit.Leases.LeaseDraftTestData;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-12: the eight steps of the wizard as the server sees them. Every data step in each state it can be in — complete, incomplete,
/// wrong — with the code of what is missing, the rule that a step is done only when the landlord has been there, the steps that follow
/// the facts (review, signature, registration) and the imported contract.
/// </summary>
public class LeaseDraftProgressTests
{
    private static LeaseDraftFlow Compute(LeaseDraftProgressInput input) => LeaseDraftProgress.Compute(input);

    // ---- a complete draft ----------------------------------------------------------------------------------------

    [Fact]
    public void ACompleteDraft_HasTheFiveDataStepsDone_AndTheReviewToDo_TheRestLocked()
    {
        var flow = Compute(Input());

        Assert.Equal(8, flow.Steps.Count);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], flow.Steps.Select(s => s.Step));
        Assert.All(Enumerable.Range(1, 5), n => Assert.Equal(LeaseStepState.Done, flow[n].State));
        Assert.Equal(LeaseStepState.Todo, flow[6].State);
        Assert.Equal([LeaseNeedCodes.ReviewNotConfirmed], NeedCodes(flow, 6));
        Assert.Equal(LeaseStepState.Locked, flow[7].State);
        Assert.Equal(LeaseStepState.Locked, flow[8].State);
        Assert.Equal([LeaseNeedCodes.ReviewNotConfirmed], AllNeedCodes(flow));
        Assert.True(flow.DataComplete);
    }

    [Fact]
    public void AStepIsDoneOnlyWhenNothingIsMissingAndTheLandlordHasBeenThere()
    {
        // Everything is typed, but the landlord reached step 3 only: steps 4 and 5 have nothing missing and are still to do.
        var flow = Compute(Input(wizardMaxStep: 3));

        Assert.Equal([LeaseStepState.Done, LeaseStepState.Done, LeaseStepState.Done, LeaseStepState.Todo, LeaseStepState.Todo],
            Enumerable.Range(1, 5).Select(n => flow[n].State));
        Assert.Empty(flow[4].Needs);
        Assert.Empty(flow[5].Needs);
        // The review waits for the data, not for the visit: nothing is missing in the first five steps.
        Assert.Equal(LeaseStepState.Todo, flow[6].State);
    }

    [Fact]
    public void ADraftWithOnlyTheProperty_HasTheStepsThatNeedSomethingToDo_WithTheirNeeds()
    {
        var values = new LeaseDraftValues { PropertyId = PropertyId };

        var flow = Compute(Input(values, wizardMaxStep: 1));

        Assert.Equal(LeaseStepState.Done, flow[1].State);
        Assert.Equal([LeaseNeedCodes.LandlordMissing, LeaseNeedCodes.TenantMissing], NeedCodes(flow, 2));
        Assert.Equal(
            [LeaseNeedCodes.ContractTypeMissing, LeaseNeedCodes.TaxRegimeMissing, LeaseNeedCodes.RentMissing, LeaseNeedCodes.DepositMissing],
            NeedCodes(flow, 3));
        Assert.Equal([LeaseNeedCodes.StartMissing], NeedCodes(flow, 4));
        Assert.Equal(LeaseStepState.Todo, flow[5].State);
        Assert.Equal([LeaseStepState.Locked, LeaseStepState.Locked, LeaseStepState.Locked], new[] { 6, 7, 8 }.Select(n => flow[n].State));
        Assert.False(flow.DataComplete);
    }

    // ---- step 1 · the property -----------------------------------------------------------------------------------

    [Fact]
    public void Step1_NoApeAndNoCadastralData_AreNeedsOfTheProperty_NotOfTheValues()
    {
        var flow = Compute(Input(facts: new LeaseDraftFacts { PropertyExists = true }));

        Assert.Equal([LeaseNeedCodes.ApeMissing, LeaseNeedCodes.CadastralMissing], NeedCodes(flow, 1));
        Assert.Equal(LeaseStepState.Todo, flow[1].State);
        Assert.Equal(LeaseStepState.Locked, flow[6].State);
        // The values are all there: the draft is complete as far as the typed state goes, the property has to be fixed.
        Assert.True(flow.DataComplete);
    }

    [Fact]
    public void Step1_APropertyThatIsNotThere_IsMissing()
    {
        var gone = Compute(Input(facts: new LeaseDraftFacts { PropertyExists = false }));
        var none = Compute(Input(Complete() with { PropertyId = null }));

        Assert.Equal([LeaseNeedCodes.PropertyMissing], NeedCodes(gone, 1));
        Assert.Equal([LeaseNeedCodes.PropertyMissing], NeedCodes(none, 1));
        Assert.False(gone.DataComplete);
    }

    // ---- step 2 · the parties ------------------------------------------------------------------------------------

    [Fact]
    public void Step2_AnIncompleteEntry_IsNamedWithItsPositionAndTheFieldsThatAreWrong()
    {
        var values = Complete() with
        {
            Landlords = [Landlord()],
            Tenants = [Tenant(), Tenant(SecondTenantFiscalCode) with { LastName = "", Residence = null }],
        };

        var flow = Compute(Input(values));

        var need = Assert.Single(flow[2].Needs);
        Assert.Equal(LeaseNeedCodes.TenantIncomplete, need.Code);
        Assert.Equal(1, need.Params!["index"]);
        Assert.Equal(["lastName", "residence"], Assert.IsAssignableFrom<IEnumerable<string>>(need.Params["fields"]));
        Assert.Equal(LeaseStepState.Todo, flow[2].State);
        Assert.False(flow.DataComplete);
    }

    [Fact]
    public void Step2_AnInvalidFiscalCode_IsAnIncompleteEntry()
    {
        var flow = Compute(Input(Complete() with { Landlords = [Landlord("RSSMRA80A01H501V")] }));

        Assert.Equal([LeaseNeedCodes.LandlordIncomplete], NeedCodes(flow, 2));
        Assert.Equal(["fiscalCode"], Assert.IsAssignableFrom<IEnumerable<string>>(flow[2].Needs[0].Params!["fields"]));
    }

    [Fact]
    public void Step2_TheSameFiscalCodeTwice_IsOneNeed_NamingTheLaterEntries()
    {
        var values = Complete() with
        {
            Landlords = [Landlord()],
            Tenants = [Tenant(), Tenant(LeaseDraftTestData.LandlordFiscalCode)],
        };

        var flow = Compute(Input(values));

        var need = Assert.Single(flow[2].Needs);
        Assert.Equal(LeaseNeedCodes.PartyFiscalCodeDuplicate, need.Code);
        var entry = Assert.Single(Assert.IsAssignableFrom<IEnumerable<object>>(need.Params!["entries"]));
        var named = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entry);
        Assert.Equal("Tenant", named["role"]);
        Assert.Equal(1, named["index"]);
    }

    [Fact]
    public void Step2_SeveralLandlordsAndTenants_AreFine()
    {
        var values = Complete() with
        {
            Landlords = [Landlord(), Landlord(SecondTenantFiscalCode)],
            Tenants = [Tenant(), Tenant(CompanyFiscalCode)],
            TaxRegime = "Ordinario",
        };

        Assert.Empty(Compute(Input(values))[2].Needs);
    }

    // ---- step 3 · type, regime, rent, deposit --------------------------------------------------------------------

    private static readonly Dictionary<string, (Func<LeaseDraftValues, LeaseDraftValues> Change, string[] Expected)> Step3Cases = new()
    {
        ["no type"] = (v => v with { ContractType = null }, [LeaseNeedCodes.ContractTypeMissing]),
        ["blank type"] = (v => v with { ContractType = "  " }, [LeaseNeedCodes.ContractTypeMissing]),
        ["a type that does not exist"] = (v => v with { ContractType = "Studenti" }, [LeaseNeedCodes.LeaseTypeNotAvailable]),
        ["no regime"] = (v => v with { TaxRegime = null }, [LeaseNeedCodes.TaxRegimeMissing]),
        ["a regime that does not exist"] = (v => v with { TaxRegime = "flat" }, [LeaseNeedCodes.TaxRegimeMissing]),
        ["no rent"] = (v => v with { MonthlyRent = null }, [LeaseNeedCodes.RentMissing]),
        ["zero rent"] = (v => v with { MonthlyRent = 0 }, [LeaseNeedCodes.RentInvalid]),
        ["negative rent"] = (v => v with { MonthlyRent = -10 }, [LeaseNeedCodes.RentInvalid]),
        ["rent over the limit"] = (v => v with { MonthlyRent = 1_000_001 }, [LeaseNeedCodes.RentInvalid]),
        ["rent with fractions of a cent"] = (v => v with { MonthlyRent = 900.123m }, [LeaseNeedCodes.RentInvalid]),
        ["negative deposit"] = (v => v with { SecurityDeposit = -1 }, [LeaseNeedCodes.DepositInvalid]),
        ["deposit of more than three rents"] = (v => v with { SecurityDeposit = 2701 }, [LeaseNeedCodes.DepositTooHigh]),
        ["a company as tenant under the cedolare secca"] = (v => v with { Tenants = [Tenant(CompanyFiscalCode)] }, [LeaseNeedCodes.CedolareNotAllowedCompany]),
        ["an unreadable rent and a deposit too big for it"] = (v => v with { MonthlyRent = -1, SecurityDeposit = 5000 }, [LeaseNeedCodes.RentInvalid]),
    };

    public static TheoryData<string> Step3CaseNames => new(Step3Cases.Keys);

    [Theory]
    [MemberData(nameof(Step3CaseNames))]
    public void Step3_EveryWayToBeWrong_IsOneNeedWithItsCode_AndTheStepIsToDo(string name)
    {
        var (change, expected) = Step3Cases[name];

        var flow = Compute(Input(change(Complete())));

        Assert.Equal(expected, NeedCodes(flow, 3));
        Assert.Equal(LeaseStepState.Todo, flow[3].State);
        Assert.Equal(LeaseStepState.Locked, flow[6].State);
        Assert.False(flow.DataComplete);
    }

    [Fact]
    public void Step3_TheDepositLimit_IsThreeRents_AndThreeRentsExactlyIsFine()
    {
        var exact = Compute(Input(Complete() with { MonthlyRent = 900m, SecurityDeposit = 2700m }));
        var over = Compute(Input(Complete() with { MonthlyRent = 900m, SecurityDeposit = 2700.01m }));

        Assert.Empty(exact[3].Needs);
        var need = Assert.Single(over[3].Needs);
        Assert.Equal(2700m, need.Params!["maxAmount"]);
        Assert.Equal(LongRentRules.DepositMaxRents, need.Params["maxRents"]);
    }

    [Fact]
    public void Step3_ACompanyAsTenantWithTheOrdinaryRegime_IsFine()
    {
        var flow = Compute(Input(Complete() with { Tenants = [Tenant(CompanyFiscalCode)], TaxRegime = "Ordinario" }));

        Assert.Empty(flow[3].Needs);
    }

    [Fact]
    public void Step3_AConcordato_NeedsItsCharacteristics()
    {
        var none = Compute(Input(Complete() with { ContractType = "Concordato" }));
        var wrong = Compute(Input(Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = 0 } }));

        Assert.Equal([LeaseNeedCodes.ConcordatoDataMissing], NeedCodes(none, 3));
        Assert.Equal([LeaseNeedCodes.ConcordatoDataInvalid], NeedCodes(wrong, 3));
    }

    [Fact]
    public void Step3_AConcordato_WithADataUnavailableComune_IsBlockedWithTheReason()
    {
        var values = Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = 70 } };
        var outcome = new LeaseDraftConcordatoOutcome { UnavailableReason = CanoneConcordatoReasonCodes.DataUnavailable };

        var flow = Compute(Input(values, concordato: outcome));

        var need = Assert.Single(flow[3].Needs);
        Assert.Equal(LeaseNeedCodes.ConcordatoUnavailable, need.Code);
        Assert.Equal(CanoneConcordatoReasonCodes.DataUnavailable, need.Params!["reason"]);
    }

    [Fact]
    public void Step3_AConcordato_RentOutsideAVerifiedRange_IsABlock_OutsideAnIndicativeOne_IsNot()
    {
        var values = Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = 70 } };
        var verified = new LeaseDraftConcordatoOutcome { RentOutOfBand = true, Indicative = false, MinMonthly = 400m, MaxMonthly = 700m };
        var indicative = verified with { Indicative = true };

        var blocked = Compute(Input(values, concordato: verified));
        var allowed = Compute(Input(values, concordato: indicative));

        var need = Assert.Single(blocked[3].Needs);
        Assert.Equal(LeaseNeedCodes.RentOutOfBand, need.Code);
        Assert.Equal(400m, need.Params!["minMonthly"]);
        Assert.Equal(700m, need.Params["maxMonthly"]);
        Assert.Empty(allowed[3].Needs);
    }

    [Fact]
    public void Step3_ARentInsideTheRange_AndATypeThatIsNotAConcordato_NeedNothingMore()
    {
        var values = Complete() with { ContractType = "Concordato", Concordato = new LeaseDraftConcordato { Sqm = 70 } };

        Assert.Empty(Compute(Input(values, concordato: new LeaseDraftConcordatoOutcome { MinMonthly = 400m, MaxMonthly = 1000m }))[3].Needs);
        // The characteristics of a draft that went back to the libero are not asked about.
        Assert.Empty(Compute(Input(Complete() with { Concordato = new LeaseDraftConcordato { Sqm = 0 } }))[3].Needs);
    }

    // ---- step 4 · the start --------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, LeaseNeedCodes.StartMissing)]
    [InlineData("", LeaseNeedCodes.StartMissing)]
    [InlineData("1 novembre", LeaseNeedCodes.StartInvalid)]
    [InlineData("2026-02-30", LeaseNeedCodes.StartInvalid)]
    [InlineData("1969-12-31", LeaseNeedCodes.StartInvalid)]
    public void Step4_AStartThatIsMissingOrNotADate_IsNamed(string? start, string expected)
    {
        var flow = Compute(Input(Complete() with { StartDate = start }));

        Assert.Equal([expected], NeedCodes(flow, 4));
        Assert.Equal(LeaseStepState.Todo, flow[4].State);
    }

    [Fact]
    public void Step4_AStartInThePast_IsNotMissingAnything_TheReviewWarnsAboutIt()
    {
        Assert.Empty(Compute(Input(Complete() with { StartDate = "2020-01-01" }))[4].Needs);
    }

    // ---- step 5 · model and attachments --------------------------------------------------------------------------

    [Fact]
    public void Step5_NothingIsRequired_ADocumentsDateInTheFuture_IsWrong()
    {
        var values = Complete() with { Tenants = [Tenant() with { DocumentsVerifiedOn = "2026-10-10" }] };

        var flow = Compute(Input(values));

        var need = Assert.Single(flow[5].Needs);
        Assert.Equal(LeaseNeedCodes.PartyDocumentsDateInvalid, need.Code);
        Assert.Equal("Tenant", need.Params!["role"]);
        Assert.Equal(0, need.Params["index"]);
        Assert.False(flow.DataComplete);
        Assert.Empty(Compute(Input(Complete() with { Tenants = [Tenant() with { DocumentsVerifiedOn = "2026-10-09" }] }))[5].Needs);
    }

    // ---- steps 6 to 8 --------------------------------------------------------------------------------------------

    [Fact]
    public void Step6_ConfirmedReview_IsDone_AndOpensTheSignature()
    {
        var flow = Compute(Input(reviewConfirmedAt: Moment));

        Assert.Equal(LeaseStepState.Done, flow[6].State);
        Assert.Empty(flow[6].Needs);
        Assert.Equal(LeaseStepState.Todo, flow[7].State);
        Assert.Equal([LeaseNeedCodes.NotSent], NeedCodes(flow, 7));
        Assert.Equal(LeaseStepState.Locked, flow[8].State);
    }

    [Fact]
    public void Step6_IsLockedWhileAnyDataStepNeedsSomething_EvenWithAConfirmedReviewOnRecord()
    {
        var flow = Compute(Input(Complete() with { MonthlyRent = null }, reviewConfirmedAt: Moment));

        Assert.Equal(LeaseStepState.Locked, flow[6].State);
        Assert.Equal(LeaseStepState.Locked, flow[7].State);
    }

    [Fact]
    public void Step7_DownloadedForSignature_WaitsForTheSignedCopy()
    {
        var flow = Compute(Input(reviewConfirmedAt: Moment, sentForSignatureAt: Moment));

        Assert.Equal(LeaseStepState.Todo, flow[7].State);
        Assert.Equal([LeaseNeedCodes.SignedCopyMissing], NeedCodes(flow, 7));
    }

    [Fact]
    public void Steps7And8_FollowTheStatusOfTheLease()
    {
        var signed = Compute(Input(status: LeaseStatus.Signed, reviewConfirmedAt: Moment));
        var registered = Compute(Input(status: LeaseStatus.Registered, reviewConfirmedAt: Moment));

        Assert.Equal(LeaseStepState.Done, signed[7].State);
        Assert.Equal(LeaseStepState.Todo, signed[8].State);
        Assert.Equal([LeaseNeedCodes.RegistrationMissing], NeedCodes(signed, 8));
        Assert.All(Enumerable.Range(1, 8), n => Assert.Equal(LeaseStepState.Done, registered[n].State));
    }

    [Fact]
    public void ALeaseThatLeftTheWizard_HasAllItsDataStepsDone_WhateverTheWizardMaxStepSays()
    {
        // A lease that exists from before the guided draft, signed: its data steps are done without anybody having visited them.
        var flow = Compute(Input(wizardMaxStep: 0, status: LeaseStatus.Signed));

        Assert.All(Enumerable.Range(1, 6), n => Assert.Equal(LeaseStepState.Done, flow[n].State));
    }

    [Fact]
    public void NoStepIsEverWaiting_ThereIsNoInviteOfTheTenantInTheFirstRelease()
    {
        foreach (var input in new[]
                 {
                     Input(),
                     Input(new LeaseDraftValues { PropertyId = PropertyId }, wizardMaxStep: 1),
                     Input(reviewConfirmedAt: Moment, sentForSignatureAt: Moment),
                     Input(origin: LeaseOrigin.Imported),
                     Input(status: LeaseStatus.Signed),
                 })
        {
            Assert.DoesNotContain(Compute(input).Steps, s => s.State == LeaseStepState.Waiting);
        }
    }

    // ---- a transitory contract made in CasaZen (TR) --------------------------------------------------------------

    /// <summary>A transitory contract of 6 months from 1 November 2026, with who has its need, the need and its documentation: nothing is missing.</summary>
    private static LeaseDraftValues ATransitory(string? end = "2027-04-30", string? documentation = "Lettera di assunzione") => Complete() with
    {
        ContractType = "Transitorio",
        TransitoryParty = "Tenant",
        TransitoryNeed = "Lavoro a Milano fino ad aprile",
        TransitoryDocumentation = documentation,
        EndDate = end,
    };

    [Theory]
    [InlineData("2026-11-30", null)] // a month, 30 days: the shortest a transitory contract can be
    [InlineData("2027-04-30", null)]
    [InlineData("2028-04-30", null)] // 18 months: the longest
    [InlineData("2028-05-01", LeaseNeedCodes.TermOutOfRange)]
    [InlineData("2026-11-29", LeaseNeedCodes.TermOutOfRange)] // less than a month
    [InlineData("2026-11-01", LeaseNeedCodes.TermOutOfRange)] // ends the day it starts
    [InlineData("2026-10-15", LeaseNeedCodes.TermOutOfRange)] // ends before it starts
    [InlineData(null, LeaseNeedCodes.EndMissing)]
    [InlineData("  ", LeaseNeedCodes.EndMissing)]
    [InlineData("next spring", LeaseNeedCodes.EndInvalid)]
    public void ATransitoryContract_HasATermOfOneToEighteenMonthsTypedByItsLandlord_Step4(string? end, string? expected)
    {
        var flow = Compute(Input(ATransitory(end)));

        string[] codes = expected is null ? [] : [expected];
        Assert.Equal(codes, NeedCodes(flow, 4));
        Assert.Equal(expected is null, flow.DataComplete);
    }

    [Theory]
    [InlineData("2026-11-30", null, null)] // 30 days: nothing to prove
    [InlineData("2026-12-01", null, LeaseNeedCodes.TransitoryDocumentationMissing)] // 31 days
    [InlineData("2026-12-01", "  ", LeaseNeedCodes.TransitoryDocumentationMissing)]
    [InlineData("2026-12-01", "Lettera di assunzione", null)]
    [InlineData("2027-04-30", null, LeaseNeedCodes.TransitoryDocumentationMissing)]
    public void ATransitoryContractOfMoreThan30Days_ProvesItsNeedWithDocumentation_Step4(string end, string? documentation, string? expected)
    {
        var flow = Compute(Input(ATransitory(end, documentation)));

        string[] codes = expected is null ? [] : [expected];
        Assert.Equal(codes, NeedCodes(flow, 4));
    }

    [Fact]
    public void ATransitoryContract_AsksWhoHasTheNeedAndWhatItIs_ADocumentationOfMoreThan300Characters_IsTooLong()
    {
        var none = Compute(Input(ATransitory() with { TransitoryParty = null, TransitoryNeed = null }));
        var wrong = Compute(Input(ATransitory() with { TransitoryParty = "Condominium", TransitoryNeed = "   " }));
        var fine = Compute(Input(ATransitory() with { TransitoryParty = "landlord" }));
        var long300 = Compute(Input(ATransitory(documentation: new string('a', 300))));
        var long301 = Compute(Input(ATransitory(documentation: new string('a', 301))));

        Assert.Equal([LeaseNeedCodes.TransitoryPartyMissing, LeaseNeedCodes.TransitoryNeedMissing], NeedCodes(none, 3));
        Assert.Equal([LeaseNeedCodes.TransitoryPartyMissing, LeaseNeedCodes.TransitoryNeedMissing], NeedCodes(wrong, 3));
        Assert.Empty(fine[3].Needs);
        Assert.Empty(long300[4].Needs);
        var tooLong = Assert.Single(long301[4].Needs);
        Assert.Equal(LeaseNeedCodes.TransitoryDocumentationTooLong, tooLong.Code);
        Assert.Equal(300, tooLong.Params!["maxLength"]);
        Assert.False(none.DataComplete);
    }

    [Fact]
    public void ATransitoryContractWithItsNeedAndItsEnd_HasNothingMissing_AndNoOtherContractAsksForTheNeed()
    {
        var flow = Compute(Input(ATransitory()));

        Assert.All(Enumerable.Range(1, 5), n => Assert.Empty(flow[n].Needs));
        Assert.True(flow.DataComplete);
        // The libero never asks for it, and a contract already signed prints nothing of its own: neither its need nor its documentation.
        Assert.Empty(Compute(Input(Complete()))[3].Needs);
        var signed = Compute(Input(ATransitory() with { TransitoryParty = null, TransitoryNeed = null, TransitoryDocumentation = null }, origin: LeaseOrigin.Imported));
        Assert.Empty(signed[3].Needs);
        Assert.Empty(signed[4].Needs);
    }

    // ---- an imported contract ------------------------------------------------------------------------------------

    [Fact]
    public void AnImportedContract_HasNoModelStepAndNoReview_AndTheSignedContractIsStep7()
    {
        var flow = Compute(Input(origin: LeaseOrigin.Imported));

        Assert.Equal(LeaseStepState.NotApplicable, flow[5].State);
        Assert.Equal(LeaseStepState.NotApplicable, flow[6].State);
        Assert.Empty(flow[5].Needs);
        Assert.Empty(flow[6].Needs);
        Assert.All(new[] { 1, 2, 3, 4 }, n => Assert.Equal(LeaseStepState.Done, flow[n].State));
        Assert.Equal(LeaseStepState.Todo, flow[7].State);
        Assert.Equal([LeaseNeedCodes.SignedCopyMissing], NeedCodes(flow, 7));
        Assert.Equal(LeaseStepState.Locked, flow[8].State);
    }

    [Fact]
    public void AnImportedContract_WithoutApeOrCadastralData_IsNotBlocked_TheReviewWarnsInstead()
    {
        var flow = Compute(Input(origin: LeaseOrigin.Imported, facts: new LeaseDraftFacts { PropertyExists = true }));

        Assert.Empty(flow[1].Needs);
        Assert.Equal(LeaseStepState.Done, flow[1].State);
        Assert.Equal(LeaseStepState.Todo, flow[7].State);
    }

    [Fact]
    public void AnImportedContract_WithMissingData_KeepsTheSignedContractLocked()
    {
        var flow = Compute(Input(Complete() with { MonthlyRent = null }, origin: LeaseOrigin.Imported));

        Assert.Equal(LeaseStepState.Locked, flow[7].State);
    }

    [Fact]
    public void AnImportedContractThatIsSigned_ThenRegistered_FollowsTheStatus()
    {
        var signed = Compute(Input(origin: LeaseOrigin.Imported, status: LeaseStatus.Signed));
        var registered = Compute(Input(origin: LeaseOrigin.Imported, status: LeaseStatus.Registered));

        Assert.Equal(LeaseStepState.Done, signed[7].State);
        Assert.Equal(LeaseStepState.Todo, signed[8].State);
        Assert.Equal(LeaseStepState.Done, registered[8].State);
    }

    // ---- the codes ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCodesOfTheNeeds_AreStableSnakeCase_AndTheListHasNoDuplicates()
    {
        var codes = typeof(LeaseNeedCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));
    }
}
