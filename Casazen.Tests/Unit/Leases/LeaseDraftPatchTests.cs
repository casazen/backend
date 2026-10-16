using System.Text.Json;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Xunit;

namespace Casazen.Tests.Unit.Leases;

/// <summary>
/// LR-12: the values of one step merged into the draft. A key that is there replaces the field, <c>null</c> clears it, a list is
/// replaced as a whole, a key that is not there leaves the field as it was; nothing is checked for being right, only for being the right
/// shape, and the entries of the parties keep the id the server gave them.
/// </summary>
public class LeaseDraftPatchTests
{
    private static readonly LeaseDraftValues Start = new() { PropertyId = LeaseDraftTestData.PropertyId };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static LeaseDraftValues Apply(LeaseDraftValues current, int step, string json, Func<Guid>? newId = null) =>
        LeaseDraftPatch.Apply(current, step, Json(json), newId ?? Guid.NewGuid);

    // ---- step 1 --------------------------------------------------------------------------------------------------

    [Fact]
    public void Step1_APropertyId_ReplacesTheProperty()
    {
        var other = Guid.NewGuid();

        var next = Apply(Start, 1, $$"""{"propertyId":"{{other}}"}""");

        Assert.Equal(other, next.PropertyId);
    }

    [Theory]
    [InlineData("""{"propertyId":null}""")]
    [InlineData("""{"propertyId":"not-an-id"}""")]
    [InlineData("""{"propertyId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"propertyId":7}""")]
    public void Step1_APropertyThatIsNotAnId_IsRefused_AContractHasOneFromTheStart(string json)
    {
        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 1, json));
    }

    [Fact]
    public void Step1_NoKey_ChangesNothing()
    {
        Assert.Equal(Start, Apply(Start, 1, "{}"));
    }

    // ---- step 2 --------------------------------------------------------------------------------------------------

    [Fact]
    public void Step2_Entries_AreKeptAsTyped_IncompleteAndWrongOnesToo()
    {
        var next = Apply(Start, 2, """
            {"landlords":[{"firstName":"Mar","lastName":"Rossi","fiscalCode":"RSSM","citizenship":"it","residence":"","contactEmail":"x"}],
             "tenants":[{"firstName":" Giulia "}]}
            """);

        var landlord = Assert.Single(next.Landlords);
        Assert.Equal("Mar", landlord.FirstName);
        Assert.Equal("RSSM", landlord.FiscalCode);
        Assert.Equal("it", landlord.Citizenship);
        Assert.Equal("", landlord.Residence);
        Assert.Equal("x", landlord.ContactEmail);
        Assert.Equal(" Giulia ", Assert.Single(next.Tenants).FirstName);
    }

    [Fact]
    public void Step2_EveryNewEntry_GetsAnIdOfItsOwn()
    {
        var ids = new Queue<Guid>([Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()]);
        var expected = ids.ToArray();

        var next = Apply(Start, 2, """{"landlords":[{"firstName":"A"}],"tenants":[{"firstName":"B"},{"firstName":"C"}]}""", ids.Dequeue);

        Assert.Equal([expected[0]], next.Landlords.Select(p => p.Id));
        Assert.Equal([expected[1], expected[2]], next.Tenants.Select(p => p.Id));
    }

    [Fact]
    public void Step2_AnEntryWithTheIdOfAnEntryOfTheDraft_KeepsItsIdAndTheDayItsDocumentsWereChecked()
    {
        var first = Apply(Start, 2, """{"tenants":[{"firstName":"Giulia","lastName":"Verdi"}]}""");
        var tenantId = first.Tenants.Single().Id;
        var verified = Apply(first, 5, $$"""{"parties":[{"id":"{{tenantId}}","documentsVerifiedOn":"2026-10-01"}]}""");

        var edited = Apply(verified, 2, $$"""{"tenants":[{"id":"{{tenantId}}","firstName":"Giulia","lastName":"Verdi-Bianchi"}]}""");

        var tenant = Assert.Single(edited.Tenants);
        Assert.Equal(tenantId, tenant.Id);
        Assert.Equal("Verdi-Bianchi", tenant.LastName);
        Assert.Equal("2026-10-01", tenant.DocumentsVerifiedOn);
    }

    [Fact]
    public void Step2_AnIdThatIsNotOfTheDraft_IsANewEntry_NeverAnotherLeasesParty()
    {
        var stranger = Guid.NewGuid();

        var next = Apply(Start, 2, $$"""{"landlords":[{"id":"{{stranger}}","firstName":"Mario"}]}""");

        Assert.NotEqual(stranger, Assert.Single(next.Landlords).Id);
    }

    [Fact]
    public void Step2_TheSameIdTwiceInOneRequest_IsUsedOnce()
    {
        var first = Apply(Start, 2, """{"tenants":[{"firstName":"Giulia"}]}""");
        var id = first.Tenants.Single().Id;

        var next = Apply(first, 2, $$"""{"tenants":[{"id":"{{id}}","firstName":"A"},{"id":"{{id}}","firstName":"B"}]}""");

        Assert.Equal(2, next.Tenants.Select(p => p.Id).Distinct().Count());
        Assert.Equal(id, next.Tenants[0].Id);
    }

    [Fact]
    public void Step2_ARowOfTheFormNobodyTypedIn_IsNotKept()
    {
        var next = Apply(Start, 2, """{"landlords":[{"firstName":"","lastName":" "},{"firstName":"Mario"}]}""");

        Assert.Equal("Mario", Assert.Single(next.Landlords).FirstName);
    }

    [Fact]
    public void Step2_OneRoleLeavesTheOtherAlone_AndNullClearsAList()
    {
        var both = Apply(Start, 2, """{"landlords":[{"firstName":"Mario"}],"tenants":[{"firstName":"Giulia"}]}""");

        var onlyLandlords = Apply(both, 2, """{"landlords":[{"firstName":"Maria"}]}""");
        var noTenants = Apply(both, 2, """{"tenants":null}""");

        Assert.Equal("Maria", onlyLandlords.Landlords.Single().FirstName);
        Assert.Equal("Giulia", onlyLandlords.Tenants.Single().FirstName);
        Assert.Empty(noTenants.Tenants);
        Assert.Equal("Mario", noTenants.Landlords.Single().FirstName);
    }

    [Fact]
    public void Step2_MoreThanTenOfARole_Is422_ThereIsALimit()
    {
        var eleven = string.Join(",", Enumerable.Range(0, 11).Select(i => $$"""{"firstName":"T{{i}}"}"""));

        var error = Assert.Throws<DomainRuleException>(() => Apply(Start, 2, $$"""{"tenants":[{{eleven}}]}"""));

        Assert.Equal("lease_parties_too_many", error.Code);
    }

    [Fact]
    public void Step2_TenOfARole_AreAccepted()
    {
        var ten = string.Join(",", Enumerable.Range(0, 10).Select(i => $$"""{"firstName":"T{{i}}"}"""));

        Assert.Equal(10, Apply(Start, 2, $$"""{"tenants":[{{ten}}]}""").Tenants.Count);
    }

    [Theory]
    [InlineData("""{"landlords":"Mario"}""")]
    [InlineData("""{"landlords":[1]}""")]
    [InlineData("""{"landlords":[{"firstName":5}]}""")]
    [InlineData("""{"landlords":[{"id":"nope","firstName":"A"}]}""")]
    [InlineData("""{"landlords":[{"documentsVerifiedOn":"2026-10-01"}]}""")] // step 5's field
    [InlineData("""{"landlords":[{"nickname":"Mario"}]}""")]
    [InlineData("""{"monthlyRent":900}""")] // step 3's field
    public void Step2_AWrongShape_IsRefused(string json)
    {
        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 2, json));
    }

    [Fact]
    public void AnyText_Over500Characters_IsRefused_ItIsTheSizeOfTheRequest_NotARuleAboutTheValue()
    {
        var long500 = new string('a', 501);
        var fine = new string('a', 500);

        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 2, $$"""{"landlords":[{"residence":"{{long500}}"}]}"""));
        Assert.Equal(500, Apply(Start, 2, $$"""{"landlords":[{"residence":"{{fine}}"}]}""").Landlords.Single().Residence!.Length);
    }

    // ---- step 3 --------------------------------------------------------------------------------------------------

    [Fact]
    public void Step3_TheEconomics_AreKeptAsTyped_NothingIsRefusedForBeingWrong()
    {
        var next = Apply(Start, 3, """{"contractType":"Transitorio","taxRegime":"x","monthlyRent":-5,"securityDeposit":99999999}""");

        Assert.Equal("Transitorio", next.ContractType);
        Assert.Equal("x", next.TaxRegime);
        Assert.Equal(-5m, next.MonthlyRent);
        Assert.Equal(99999999m, next.SecurityDeposit);
    }

    [Fact]
    public void Step3_AKeyThatIsNotThere_LeavesTheFieldAsItWas_NullClearsIt()
    {
        var typed = Apply(Start, 3, """{"contractType":"Libero","taxRegime":"CedolareSecca","monthlyRent":900,"securityDeposit":1800}""");

        var onlyRent = Apply(typed, 3, """{"monthlyRent":950}""");
        var noDeposit = Apply(typed, 3, """{"securityDeposit":null}""");

        Assert.Equal(950m, onlyRent.MonthlyRent);
        Assert.Equal("Libero", onlyRent.ContractType);
        Assert.Equal(1800m, onlyRent.SecurityDeposit);
        Assert.Null(noDeposit.SecurityDeposit);
        Assert.Equal(900m, noDeposit.MonthlyRent);
    }

    [Fact]
    public void Step3_TheCharacteristicsOfTheConcordato_AreReadWithTheirDefaults()
    {
        var next = Apply(Start, 3, """{"concordato":{"sqm":70.5,"typeAElementCount":2,"isFurnished":true,"zoneName":"Centro"}}""");

        var concordato = next.Concordato!;
        Assert.Equal(70.5m, concordato.Sqm);
        Assert.Equal(2, concordato.TypeAElementCount);
        Assert.True(concordato.IsFurnished);
        Assert.False(concordato.StoveHeating);
        Assert.Equal(0m, concordato.GarageSqm);
        Assert.Equal("Centro", concordato.ZoneName);
        Assert.Null(Apply(next, 3, """{"concordato":null}""").Concordato);
    }

    [Theory]
    [InlineData("""{"monthlyRent":"900"}""")] // a number is a number
    [InlineData("""{"monthlyRent":true}""")]
    [InlineData("""{"contractType":1}""")]
    [InlineData("""{"concordato":{"sqm":"big"}}""")]
    [InlineData("""{"concordato":{"typeAElementCount":1.5}}""")]
    [InlineData("""{"concordato":{"isFurnished":"yes"}}""")]
    [InlineData("""{"concordato":{"floors":2}}""")]
    [InlineData("""{"concordato":[]}""")]
    [InlineData("""{"startDate":"2026-11-01"}""")] // step 4's field
    public void Step3_AWrongShape_IsRefused(string json)
    {
        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 3, json));
    }

    // ---- step 4 --------------------------------------------------------------------------------------------------

    [Fact]
    public void Step4_TheStartDate_IsKeptAsTyped_EvenWhenItIsNotADate()
    {
        Assert.Equal("2026-11-01", Apply(Start, 4, """{"startDate":"2026-11-01"}""").StartDate);
        Assert.Equal("1/11/26", Apply(Start, 4, """{"startDate":"1/11/26"}""").StartDate);
        Assert.Null(Apply(Apply(Start, 4, """{"startDate":"2026-11-01"}"""), 4, """{"startDate":null}""").StartDate);
    }

    [Fact]
    public void Step4_ANumberInsteadOfAText_IsRefused()
    {
        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 4, """{"startDate":20261101}"""));
    }

    // ---- step 5 --------------------------------------------------------------------------------------------------

    [Fact]
    public void Step5_TheDayTheDocumentsWereChecked_IsSetPerParty_ByItsId()
    {
        var typed = Apply(Start, 2, """{"landlords":[{"firstName":"Mario"}],"tenants":[{"firstName":"Giulia"},{"firstName":"Marco"}]}""");
        var landlord = typed.Landlords.Single().Id;
        var second = typed.Tenants[1].Id;

        var next = Apply(typed, 5, $$"""{"parties":[{"id":"{{landlord}}","documentsVerifiedOn":"2026-10-01"},{"id":"{{second}}","documentsVerifiedOn":"2026-10-02"}]}""");

        Assert.Equal("2026-10-01", next.Landlords.Single().DocumentsVerifiedOn);
        Assert.Null(next.Tenants[0].DocumentsVerifiedOn);
        Assert.Equal("2026-10-02", next.Tenants[1].DocumentsVerifiedOn);
        Assert.Null(Apply(next, 5, $$"""{"parties":[{"id":"{{second}}","documentsVerifiedOn":null}]}""").Tenants[1].DocumentsVerifiedOn);
    }

    [Fact]
    public void Step5_AnIdThatIsNotAPartyOfTheDraft_IsRefused()
    {
        var typed = Apply(Start, 2, """{"landlords":[{"firstName":"Mario"}]}""");

        Assert.Throws<LeaseDraftValuesException>(() =>
            Apply(typed, 5, $$"""{"parties":[{"id":"{{Guid.NewGuid()}}","documentsVerifiedOn":"2026-10-01"}]}"""));
        Assert.Throws<LeaseDraftValuesException>(() => Apply(typed, 5, """{"parties":[{"documentsVerifiedOn":"2026-10-01"}]}"""));
    }

    // ---- the step ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void AStepThatIsNotADataStep_IsRefused(int step)
    {
        Assert.Throws<LeaseDraftValuesException>(() => LeaseDraftPatch.Apply(Start, step, Json("{}"), Guid.NewGuid));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"values\"")]
    public void ValuesThatAreNotAnObject_AreRefused(string json)
    {
        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 1, json));
    }

    [Fact]
    public void TheKeysAreReadWithoutRegardToCase_AndATwiceWrittenKeyIsRefused()
    {
        Assert.Equal(900m, Apply(Start, 3, """{"MonthlyRent":900}""").MonthlyRent);
        Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 3, """{"monthlyRent":900,"MONTHLYRENT":950}"""));
    }

    [Fact]
    public void TheMessageOfARefusal_NamesTheField_NeverWhatWasSent()
    {
        var error = Assert.Throws<LeaseDraftValuesException>(() => Apply(Start, 2, """{"landlords":[{"firstName":7,"fiscalCode":"RSSMRA80A01H501U"}]}"""));

        Assert.Contains("landlords[0].firstName", error.Message);
        Assert.DoesNotContain("RSSMRA", error.Message);
    }
}
